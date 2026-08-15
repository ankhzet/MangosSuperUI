using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Read-only queries against item_template + item_source resolution. Mirrors
/// the search/detail/sources endpoints of <c>ItemsController</c>.
/// </summary>
[McpServerToolType]
public class ItemTools
{
    private const int CustomRangeStart = 900000;

    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly ILogger<ItemTools> _log;

    public ItemTools(ConnectionFactory db, DbcService dbc, ILogger<ItemTools> log)
    {
        _db = db;
        _dbc = dbc;
        _log = log;
    }

    [McpServerTool(Name = "item_search")]
    [Description(
        "Paginated item search across item_template. Numeric `query` matches entry id; " +
        "non-numeric matches name substring. All UI filters supported (class/subclass/" +
        "quality/inventory/level range/custom only/has display). Returns icon URLs in " +
        "an `icons` map keyed by display_id.")]
    public async Task<string> Search(
        [Description("Search term (entry id or name substring).")] string? query = null,
        [Description("item class id.")] int? classFilter = null,
        [Description("item subclass id.")] int? subclassFilter = null,
        [Description("item quality (0=poor .. 5=legendary).")] int? qualityFilter = null,
        [Description("inventory type (slot).")] int? inventoryTypeFilter = null,
        [Description("min required_level.")] int? minLevel = null,
        [Description("max required_level.")] int? maxLevel = null,
        [Description("min item_level.")] int? minItemLevel = null,
        [Description("max item_level.")] int? maxItemLevel = null,
        [Description("true = only items in 900000+ range.")] bool? customOnly = null,
        [Description("true = only rows with display_id > 0.")] bool? hasDisplay = null,
        [Description("entry|name|quality|itemLevel|requiredLevel|dps")] string? sort = null,
        [Description("asc|desc")] string? dir = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 100.")] int pageSize = 50)
    {
        var capped = Math.Clamp(pageSize, 1, 100);
        try
        {
            using var conn = _db.Mangos();
            var where = "WHERE patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)";
            var p = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(query))
            {
                if (uint.TryParse(query.Trim(), out var entryId))
                {
                    where += " AND entry = @EntryId";
                    p.Add("EntryId", entryId);
                }
                else
                {
                    where += " AND name LIKE @Search";
                    p.Add("Search", $"%{query.Trim()}%");
                }
            }
            if (classFilter.HasValue) { where += " AND class = @Class"; p.Add("Class", classFilter.Value); }
            if (subclassFilter.HasValue) { where += " AND subclass = @Subclass"; p.Add("Subclass", subclassFilter.Value); }
            if (qualityFilter.HasValue) { where += " AND quality = @Quality"; p.Add("Quality", qualityFilter.Value); }
            if (inventoryTypeFilter.HasValue) { where += " AND inventory_type = @InvType"; p.Add("InvType", inventoryTypeFilter.Value); }
            if (minLevel.HasValue) { where += " AND required_level >= @MinLevel"; p.Add("MinLevel", minLevel.Value); }
            if (maxLevel.HasValue) { where += " AND required_level <= @MaxLevel"; p.Add("MaxLevel", maxLevel.Value); }
            if (minItemLevel.HasValue) { where += " AND item_level >= @MinIlvl"; p.Add("MinIlvl", minItemLevel.Value); }
            if (maxItemLevel.HasValue) { where += " AND item_level <= @MaxIlvl"; p.Add("MaxIlvl", maxItemLevel.Value); }
            if (customOnly == true) { where += " AND entry >= @CustomStart"; p.Add("CustomStart", CustomRangeStart); }
            if (hasDisplay == true) where += " AND display_id > 0";

            var sortColumn = (sort ?? "entry").ToLowerInvariant() switch
            {
                "name" => "name",
                "quality" => "quality",
                "itemlevel" => "item_level",
                "requiredlevel" => "required_level",
                "dps" => "((dmg_min1 + dmg_max1) / 2) / (NULLIF(delay, 0) / 1000)",
                _ => "entry"
            };
            var sortDir = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            var orderBy = sortColumn == "entry" ? $"entry {sortDir}" : $"{sortColumn} {sortDir}, entry ASC";

            var totalCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM item_template {where}", p);
            p.Add("Offset", (page - 1) * capped);
            p.Add("PageSize", capped);

            var dataSql = $@"
                SELECT entry, name, class, subclass, quality, display_id AS displayId,
                       inventory_type AS inventoryType, required_level AS requiredLevel,
                       item_level AS itemLevel, description,
                       buy_price AS buyPrice, sell_price AS sellPrice,
                       bonding, stackable, max_count AS maxCount,
                       armor, block,
                       dmg_min1 AS dmgMin1, dmg_max1 AS dmgMax1, dmg_type1 AS dmgType1, delay,
                       stat_type1 AS statType1, stat_value1 AS statValue1,
                       stat_type2 AS statType2, stat_value2 AS statValue2,
                       stat_type3 AS statType3, stat_value3 AS statValue3,
                       stat_type4 AS statType4, stat_value4 AS statValue4,
                       stat_type5 AS statType5, stat_value5 AS statValue5
                FROM item_template {where}
                ORDER BY {orderBy}
                LIMIT @PageSize OFFSET @Offset";

            var rows = (await conn.QueryAsync<dynamic>(dataSql, p)).ToList();
            var iconMap = new Dictionary<uint, string>();
            foreach (var item in rows)
            {
                uint did = (uint)(item.displayId ?? 0);
                if (did > 0 && !iconMap.ContainsKey(did))
                    iconMap[did] = _dbc.GetItemIconPath(did);
            }
            return McpResult.Success(new
            {
                items = rows,
                icons = iconMap,
                totalCount,
                page,
                pageSize = capped,
                totalPages = (int)Math.Ceiling((double)totalCount / capped),
                sort = sortColumn,
                dir = sortDir
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_search failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "item_detail")]
    [Description(
        "Full item record by entry id. Includes icon URL, model info, all 10 stat " +
        "slots, all 5 spell trigger slots, and basic combat/armor fields.")]
    public async Task<string> Detail(
        [Description("Item entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT entry, name, class, subclass, quality, display_id AS displayId,
                         inventory_type AS inventoryType, description,
                         buy_price AS buyPrice, sell_price AS sellPrice,
                         bonding, stackable, max_count AS maxCount,
                         required_level AS requiredLevel, item_level AS itemLevel,
                         required_skill AS requiredSkill, required_skill_rank AS requiredSkillRank,
                         allowable_class AS allowableClass, allowable_race AS allowableRace,
                         armor, block,
                         dmg_min1 AS dmgMin1, dmg_max1 AS dmgMax1, dmg_type1 AS dmgType1,
                         dmg_min2 AS dmgMin2, dmg_max2 AS dmgMax2, dmg_type2 AS dmgType2,
                         delay, range_mod AS rangeMod, ammo_type AS ammoType,
                         stat_type1 AS statType1, stat_value1 AS statValue1,
                         stat_type2 AS statType2, stat_value2 AS statValue2,
                         stat_type3 AS statType3, stat_value3 AS statValue3,
                         stat_type4 AS statType4, stat_value4 AS statValue4,
                         stat_type5 AS statType5, stat_value5 AS statValue5,
                         spellid_1 AS spellId1, spelltrigger_1 AS spellTrigger1,
                         spellid_2 AS spellId2, spelltrigger_2 AS spellTrigger2,
                         spellid_3 AS spellId3, spelltrigger_3 AS spellTrigger3,
                         max_durability AS maxDurability,
                         material, sheath,
                         start_quest AS startQuest, lock_id AS lockId,
                         random_property AS randomProperty, set_id AS setId,
                         disenchant_id AS disenchantId
                  FROM item_template WHERE entry = @entry
                  ORDER BY patch DESC LIMIT 1",
                new { entry });

            if (row is null)
                return McpResult.Success(new { found = false, entry }).ToJson();

            uint did = (uint)(row.displayId ?? 0);
            var iconUrl = did > 0 ? _dbc.GetItemIconPath(did) : null;
            var modelInfo = did > 0 ? _dbc.GetItemModelInfo(did) : null;

            return McpResult.Success(new { found = true, entry, row, iconUrl, modelInfo }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_detail failed for entry {Entry}", entry);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "item_sources")]
    [Description(
        "Every way to obtain an item: creature drops (including loot behind ref_loot_template " +
        "chains), pickpocket, skinning, gameobject, container, vendor, quest (reward/choice/" +
        "objective/starts), crafting spells, disenchant. Returns per-bucket lists and " +
        "a notes list explaining any probes that failed. Single most useful item tool.")]
    public async Task<string> Sources(
        [Description("Item entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var result = await ItemSourceResolver.ResolveAsync(conn, entry);
            return McpResult.Success(new
            {
                success = result.Success,
                entry = result.Entry,
                totalCount = result.TotalCount,
                creatures = result.Creatures,
                objects = result.Objects,
                containers = result.Containers,
                vendors = result.Vendors,
                quests = result.Quests,
                crafted = result.Crafted,
                disenchant = result.Disenchant,
                notes = result.Notes
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_sources failed for entry {Entry}", entry);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "item_icon_search")]
    [Description(
        "Search item icons by filename substring (e.g. 'inv_sword'). Returns up to " +
        "`limit` display IDs that share that icon. Useful for matching a custom item's " +
        "icon to a vanilla family.")]
    public async Task<string> IconSearch(
        [Description("Icon filename substring, case-insensitive.")] string query,
        [Description("Max results (hard-capped at 200).")] int limit = 60)
    {
        if (string.IsNullOrWhiteSpace(query))
            return McpResult.Failure(ErrorCodes.InvalidInput, "query cannot be empty").ToJson();
        var capped = Math.Clamp(limit, 1, 200);
        try
        {
            var hits = new List<object>();
            int scanned = 0;
            foreach (var (did, icon) in _dbc.ItemDisplayIcons)
            {
                scanned++;
                if (icon.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new { displayId = did, icon });
                    if (hits.Count >= capped) break;
                }
            }
            return McpResult.Success(new { query, matched = hits.Count, scanned, results = hits }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_icon_search failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "item_next_custom_id")]
    [Description(
        "Next free entry in the 900000+ custom-item range. Wraps ItemsController.NextCustomId.")]
    public async Task<string> NextCustomId()
    {
        try
        {
            using var conn = _db.Mangos();
            var max = await conn.ExecuteScalarAsync<int?>(
                $"SELECT MAX(entry) FROM item_template WHERE entry >= {CustomRangeStart}");
            var next = (max ?? CustomRangeStart - 1) + 1;
            return McpResult.Success(new { nextEntry = next }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_next_custom_id failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
