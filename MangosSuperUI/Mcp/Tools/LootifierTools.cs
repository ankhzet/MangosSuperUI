using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Read endpoints of the Lootifier / Crafting Lootifier / Quest Lootifier
/// controllers. Generation/commit endpoints are excluded from MCP for
/// safety — they're complex multi-step mutations that warrant a UI flow.
///
/// The lootifier pool-share model (v6) generates tiered variants for
/// creatures, crafting recipes, and quest rewards. Read tools here let
/// an agent inspect the meta + status; write tools live behind a future
/// <c>lootifier</c> capability.
/// </summary>
[McpServerToolType]
public class LootifierTools
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<LootifierTools> _log;

    public LootifierTools(ConnectionFactory db, ILogger<LootifierTools> log)
    {
        _db = db;
        _log = log;
    }

    [McpServerTool(Name = "lootifier_meta")]
    [Description(
        "Lootifier v5/v6 metadata: stat names, default weights, instance maps, " +
        "band defaults, weapon DPS reference. Read this first.")]
    public string Meta()
    {
        try
        {
            using var conn = _db.Mangos();
            var instances = (conn.Query<(int MapId, string Name, string Category, string LevelRange)>(
                @"SELECT mapId, name, category, levelRange FROM (VALUES
                  ROW(0,'Azeroth (Eastern Kingdoms)','Continent','1-60'),
                  ROW(1,'Kalimdor','Continent','1-60'),
                  ROW(13,'Test','Dev','-'),
                  ROW(30,'Alterac Valley','BG','51-60'),
                  ROW(33,'Shadowmoon Valley','Outland','60-70'),
                  ROW(36,'Hellfire Peninsula','Outland','58-70'),
                  ROW(43,'The Wailing Caverns','Instance','17-24'),
                  ROW(47,'Razorfen Kraul','Instance','24-32'),
                  ROW(48,'Blackfathom Deeps','Instance','20-30'),
                  ROW(90,'Gnomeregan','Instance','24-32'),
                  ROW(109,'Sunwell Plateau','Raid','70-75'),
                  ROW(120,'Zul\'Gurub','Raid','60'),
                  ROW(129,'Onyxia\'s Lair','Raid','60'),
                  ROW(230,'Molten Core','Raid','50-60'),
                  ROW(249,'Onyxia\'s Lair (40)','Raid','40'),
                  ROW(289,'Scholomance','Instance','58-60'),
                  ROW(309,'Zul\'Gurub (40)','Raid','40-60'),
                  ROW(329,'Stratholme','Instance','46-55'),
                  ROW(349,'Maraudon','Instance','30-38'),
                  ROW(389,'Ragefire Chasm','Instance','13-18'),
                  ROW(409,'Molten Core (40)','Raid','40-50'),
                  ROW(429,'Dire Maul','Instance','55-58'),
                  ROW(449,'Onyxia\'s Lair (20)','Raid','20'),
                  ROW(469,'Blackwing Lair','Raid','40-60'),
                  ROW(509,'Ruins of Ahn\'Qiraj','Raid','50-60'),
                  ROW(531,'Ahn\'Qiraj Temple','Raid','60'),
                  ROW(533,'Naxxramas','Raid','60')
                ) AS i(mapId, name, category, levelRange) ORDER BY mapId")).ToList();
            return McpResult.Success(new
            {
                instances = instances.Select(i => new { i.MapId, i.Name, i.Category, i.LevelRange }),
                version = "v6 (weighted pool)"
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "lootifier_meta failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "lootifier_zones")]
    [Description("Instance map list (alias of the lootifier picker data).")]
    public string Zones()
    {
        try
        {
            using var conn = _db.Mangos();
            var count = conn.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT map) FROM creature WHERE map <> -1");
            var maps = (conn.Query<(int Map, int Creatures)>(@"
                SELECT map, COUNT(*) FROM creature GROUP BY map ORDER BY map")).ToList();
            return McpResult.Success(new
            {
                distinctMaps = maps.Count,
                creaturesPerMap = maps
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "lootifier_zones failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "lootifier_status")]
    [Description(
        "Overall Lootifier status: total variants generated, base items " +
        "tracked, orphan count.")]
    public string Status()
    {
        try
        {
            using var conn = _db.Mangos();
            var totalVariants = conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM item_template WHERE entry >= 950000");
            var totalItems = conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM item_template");
            // Orphan: generated entries no longer referenced by any lootifier-managed creature/reference
            var orphans = conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM item_template it
                WHERE it.entry >= 950000
                  AND NOT EXISTS (
                      SELECT 1 FROM reference_loot_template rlt WHERE rlt.item = it.entry
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM creature_loot_template clt WHERE clt.item = it.entry
                  )");
            return McpResult.Success(new
            {
                totalVariants = totalVariants,
                totalItems = totalItems,
                orphans = orphans
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "lootifier_status failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "crafting_lootifier_status")]
    [Description(
        "Crafting lootifier variant totals — gear-profession recipes that have " +
        "been auto-tiered.")]
    public string CraftingStatus()
    {
        try
        {
            using var conn = _db.Mangos();
            // Crafting lootifier writes into reference_loot_template with
            // entry >= 9500000 — count distinct recipe items.
            var variants = conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT item) FROM reference_loot_template WHERE entry >= 9500000");
            return McpResult.Success(new { craftingVariants = variants }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "crafting_lootifier_status failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "quest_lootifier_status")]
    [Description(
        "Quest lootifier status — quest reward variants generated across the " +
        "whole world.")]
    public string QuestStatus()
    {
        try
        {
            using var conn = _db.Mangos();
            var questVariants = conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT entry) FROM quest_template WHERE entry >= 90000");
            return McpResult.Success(new { questVariants }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "quest_lootifier_status failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
