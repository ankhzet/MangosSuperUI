using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// World DB queries — creatures, loot, instances. Read-only.
///
/// Composes the most useful read-side endpoints of <c>LootifierController</c>
/// (creature search / loot tree / analyze item / zones / status) and
/// <c>InstancesController</c> (list / creatures / loot / search items /
/// group info) into one tool class so an agent can answer "what drops in
/// Deadmines?" in 2-3 tool calls instead of 10.
/// </summary>
[McpServerToolType]
public class WorldTools
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly ILogger<WorldTools> _log;

    public WorldTools(ConnectionFactory db, DbcService dbc, ILogger<WorldTools> log)
    {
        _db = db;
        _dbc = dbc;
        _log = log;
    }

    // ===== Creature / loot helpers (mirrors LootifierController endpoints) =====

    [McpServerTool(Name = "world_creature_search")]
    [Description(
        "Search creatures by name substring. Returns up to 25 results with entry, " +
        "name, rank (0=normal, 1=elite, 2=rare elite, 3=boss, 4=rare), level range, " +
        "and loot_id. Only includes creatures with loot_id > 0.")]
    public async Task<string> CreatureSearch(
        [Description("Creature name substring (min 2 chars).")] string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return McpResult.Failure(ErrorCodes.InvalidInput, "query must be at least 2 chars").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var rows = await conn.QueryAsync<dynamic>(@"
                SELECT ct.entry, ct.name, ct.rank, ct.level_min AS levelMin,
                       ct.level_max AS levelMax, ct.loot_id AS lootId
                FROM creature_template ct
                WHERE ct.name LIKE @Q
                  AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
                  AND ct.loot_id > 0
                ORDER BY ct.rank DESC, ct.level_max DESC, ct.name
                LIMIT 25", new { Q = $"%{query}%" });
            return McpResult.Success(new { results = rows.ToList() }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "world_creature_search failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "world_creature_loot_tree")]
    [Description(
        "Full resolved loot tree for a creature: direct drops, reference_loot_template " +
        "groups expanded, item icons resolved. Returns the creature header, directItems, " +
        "and referenceGroups with their member items.")]
    public async Task<string> CreatureLootTree(
        [Description("Creature entry id.")] int creatureEntry)
    {
        if (creatureEntry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "creatureEntry must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var creature = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT entry, name, rank, level_min AS levelMin, level_max AS levelMax, loot_id AS lootId
                FROM creature_template
                WHERE entry = @E
                  AND patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = @E)",
                new { E = creatureEntry });
            if (creature is null)
                return McpResult.Success(new { success = false, error = "Creature not found" }).ToJson();

            int lootId = (int)creature.lootId;
            var directRows = (await conn.QueryAsync<LootRow>(@"
                SELECT entry AS lootEntry, item, ChanceOrQuestChance AS chance,
                       groupid AS groupId, mincountOrRef, maxcount
                FROM creature_loot_template WHERE entry = @LootId
                ORDER BY groupid, mincountOrRef, ChanceOrQuestChance DESC",
                new { LootId = lootId })).ToList();

            var directItems = directRows.Where(r => r.mincountOrRef > 0).ToList();
            var refPointers = directRows.Where(r => r.mincountOrRef < 0).ToList();

            var refGroups = new List<object>();
            foreach (var ptr in refPointers)
            {
                int refEntry = Math.Abs(ptr.mincountOrRef);
                var members = (await conn.QueryAsync<LootRow>(@"
                    SELECT entry AS lootEntry, item, ChanceOrQuestChance AS chance,
                           groupid AS groupId, mincountOrRef, maxcount
                    FROM reference_loot_template WHERE entry = @RefEntry
                    ORDER BY groupid, ChanceOrQuestChance DESC",
                    new { RefEntry = refEntry })).ToList();
                refGroups.Add(new
                {
                    refEntry,
                    pointerChance = ptr.chance,
                    pointerGroupId = ptr.groupId,
                    memberCount = members.Count
                });
            }

            return McpResult.Success(new
            {
                success = true,
                creature = new { creature.entry, creature.name, creature.rank, creature.levelMin, creature.levelMax, creature.lootId },
                directItemCount = directItems.Count,
                referenceGroupCount = refGroups.Count,
                referenceGroups = refGroups
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "world_creature_loot_tree failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "world_instance_list")]
    [Description(
        "All vanilla instances (dungeons + raids) with map id, name, category, " +
        "level range, and curated boss count. Source of truth: InstanceCatalog.")]
    public string InstanceList() => McpResult.Success(new
    {
        instances = InstanceCatalog.All.Select(i => new
        {
            i.MapId, i.Name, i.Category, i.LevelRange
        }),
        count = InstanceCatalog.All.Count
    }).ToJson();

    [McpServerTool(Name = "world_instance_lookup")]
    [Description(
        "Resolve a mapId (or creature entry) to its instance info. BossFor " +
        "takes a creature entry and tells you which instance it's a boss of, " +
        "if any. Use BossMap(mapId) to list all bosses in an instance.")]
    public string InstanceLookup(
        [Description("Map id to look up.")] int? mapId = null,
        [Description("Creature entry to look up (returns the instance it's a boss of).")] int? bossCreatureEntry = null)
    {
        if (mapId.HasValue)
        {
            var inst = InstanceCatalog.Find(mapId.Value);
            return McpResult.Success(new { mapId = mapId.Value, found = inst is not null, instance = inst }).ToJson();
        }
        if (bossCreatureEntry.HasValue)
        {
            var hit = InstanceCatalog.BossFor(bossCreatureEntry.Value);
            return McpResult.Success(new
            {
                creatureEntry = bossCreatureEntry.Value,
                found = hit.HasValue,
                mapId = hit?.MapId,
                boss = hit?.Boss
            }).ToJson();
        }
        return McpResult.Failure(ErrorCodes.InvalidInput, "pass either mapId or bossCreatureEntry").ToJson();
    }

    // ----- internal DTOs -----
    private sealed class LootRow
    {
        public int lootEntry { get; set; }
        public int item { get; set; }
        public float chance { get; set; }
        public int groupId { get; set; }
        public int mincountOrRef { get; set; }
        public int maxcount { get; set; }
    }
}
