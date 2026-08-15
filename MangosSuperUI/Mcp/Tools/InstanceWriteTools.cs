using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Loot-table writes. All four tools require <c>write_db</c> and route
/// every change through <see cref="AuditService"/> with before/after
/// state captured.
/// </summary>
[McpServerToolType]
public class InstanceWriteTools
{
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<InstanceWriteTools> _log;

    public InstanceWriteTools(ConnectionFactory db, AuditService audit,
        McpCallContext ctx, ILogger<InstanceWriteTools> log)
    {
        _db = db;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "instance_update_loot")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Update a single loot row's chance and/or count fields. Source " +
        "selects between creature_loot_template (direct) and " +
        "reference_loot_template (pool). Requires the full key (entry, " +
        "item, groupId, patchMin, patchMax) to disambiguate.")]
    public async Task<string> UpdateLoot(
        [Description("Loot table entry id.")] int entry,
        [Description("Item template entry id.")] int item,
        [Description("Group id within the loot table.")] int groupId,
        [Description("Patch min.")] int patchMin,
        [Description("Patch max.")] int patchMax,
        [Description("'direct' or 'reference'.")] string source = "direct",
        [Description("New ChanceOrQuestChance.")] float? newChance = null,
        [Description("New maxcount.")] int? newMaxCount = null,
        [Description("New mincountOrRef.")] int? newMinCount = null)
    {
        var table = source == "reference" ? "reference_loot_template" : "creature_loot_template";
        try
        {
            using var conn = _db.Mangos();
            var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
                $@"SELECT * FROM `{table}`
                   WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                     AND patch_min = @PatchMin AND patch_max = @PatchMax",
                new { Entry = entry, Item = item, GroupId = groupId, PatchMin = patchMin, PatchMax = patchMax });
            if (before is null)
                return McpResult.Failure(ErrorCodes.NotFound, "Loot row not found").ToJson();

            var setClauses = new List<string>();
            var p = new DynamicParameters();
            p.Add("Entry", entry); p.Add("Item", item); p.Add("GroupId", groupId);
            p.Add("PatchMin", patchMin); p.Add("PatchMax", patchMax);
            if (newChance.HasValue) { setClauses.Add("ChanceOrQuestChance = @NewChance"); p.Add("NewChance", newChance.Value); }
            if (newMaxCount.HasValue) { setClauses.Add("maxcount = @NewMaxCount"); p.Add("NewMaxCount", newMaxCount.Value); }
            if (newMinCount.HasValue) { setClauses.Add("mincountOrRef = @NewMinCount"); p.Add("NewMinCount", newMinCount.Value); }
            if (setClauses.Count == 0)
                return McpResult.Failure(ErrorCodes.InvalidInput, "No changes specified").ToJson();

            await conn.ExecuteAsync(
                $@"UPDATE `{table}` SET {string.Join(", ", setClauses)}
                   WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                     AND patch_min = @PatchMin AND patch_max = @PatchMax", p);

            var after = await conn.QueryFirstOrDefaultAsync<dynamic>(
                $@"SELECT * FROM `{table}`
                   WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                     AND patch_min = @PatchMin AND patch_max = @PatchMax",
                new { Entry = entry, Item = item, GroupId = groupId, PatchMin = patchMin, PatchMax = patchMax });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "loot_edit",
                TargetType = "loot_row",
                TargetName = $"Item #{item}",
                TargetId = item,
                StateBefore = System.Text.Json.JsonSerializer.Serialize((IDictionary<string, object>)before),
                StateAfter = after is null ? null : System.Text.Json.JsonSerializer.Serialize((IDictionary<string, object>)after),
                IsReversible = true,
                Success = true,
                Notes = $"Edited loot in {table} entry={entry}, item={item}"
            });

            return McpResult.Success(new { success = true, table, entry, item }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "instance_update_loot failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "instance_multiply_creature_loot")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Bulk multiplier on all direct drop chances for a creature's loot " +
        "table. Multiplier > 1 = rarer drops become rarer; < 1 = more common. " +
        "Negative-chance (quest) rows are also multiplied, clamped to [-100, 100]. " +
        "Reference-table pointers are skipped.")]
    public async Task<string> MultiplyCreatureLoot(
        [Description("Creature entry id.")] int creatureEntry,
        [Description("Multiplier (e.g. 0.5 doubles odds, 2.0 halves them).")] float multiplier)
    {
        if (multiplier <= 0 || multiplier > 100)
            return McpResult.Failure(ErrorCodes.InvalidInput, "multiplier must be between 0.01 and 100").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var lootId = await conn.ExecuteScalarAsync<int?>(
                "SELECT loot_id FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = creatureEntry });
            if (lootId is null || lootId.Value == 0)
                return McpResult.Failure(ErrorCodes.NotFound, "Creature has no loot table").ToJson();

            var rows = (await conn.QueryAsync<dynamic>(@"
                SELECT entry, item, ChanceOrQuestChance AS chance, groupid, patch_min, patch_max
                FROM creature_loot_template
                WHERE entry = @LootId AND mincountOrRef > 0 AND ABS(ChanceOrQuestChance) < 100",
                new { LootId = lootId.Value })).ToList();
            if (rows.Count == 0)
                return McpResult.Success(new { success = true, totalUpdated = 0 }).ToJson();

            var beforeState = rows.Select(r => new
            {
                item = (int)r.item,
                groupid = (int)r.groupid,
                chance = (float)r.chance
            }).ToList();

            int updated = 0;
            foreach (var row in rows)
            {
                float oldChance = (float)row.chance;
                float newChance = oldChance < 0
                    ? Math.Max(-100f, oldChance * multiplier)
                    : Math.Min(100f, oldChance * multiplier);
                newChance = (float)Math.Round(newChance, 4);
                await conn.ExecuteAsync(
                    @"UPDATE creature_loot_template
                      SET ChanceOrQuestChance = @NewChance
                      WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                        AND patch_min = @PatchMin AND patch_max = @PatchMax",
                    new
                    {
                        NewChance = newChance,
                        Entry = (int)row.entry,
                        Item = (int)row.item,
                        GroupId = (int)row.groupid,
                        PatchMin = (int)row.patch_min,
                        PatchMax = (int)row.patch_max
                    });
                updated++;
            }

            var creatureName = await conn.ExecuteScalarAsync<string>(
                "SELECT name FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = creatureEntry }) ?? $"Creature #{creatureEntry}";

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "loot_multiply_creature",
                TargetType = "creature_loot",
                TargetName = creatureName,
                TargetId = creatureEntry,
                StateBefore = System.Text.Json.JsonSerializer.Serialize(beforeState),
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new { multiplier, updated }),
                IsReversible = true,
                Success = true,
                Notes = $"Applied {multiplier}x to {creatureName}'s loot ({updated} rows)"
            });

            return McpResult.Success(new { success = true, creatureEntry, multiplier, totalUpdated = updated }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "instance_multiply_creature_loot failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "instance_add_loot_item")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Insert an item into a creature's loot table. If `refEntry` is set, " +
        "insert into that reference_loot_template entry instead of the " +
        "creature's direct loot_id. Refuses to duplicate an existing (item, " +
        "groupId) row.")]
    public async Task<string> AddLootItem(
        [Description("Creature entry id (ignored if refEntry is set).")] int creatureEntry,
        [Description("Item template entry id to add.")] int itemEntry,
        [Description("Drop chance (0..100).")] float chance,
        [Description("Group id within the loot table (default 0).")] int groupId = 0,
        [Description("Min drop count (default 1).")] int minCount = 1,
        [Description("Max drop count (default 1).")] int maxCount = 1,
        [Description("Patch min (default 0).")] int patchMin = 0,
        [Description("Patch max (default 10).")] int patchMax = 10,
        [Description("If set, insert into this reference_loot_template entry.")] int? refEntry = null)
    {
        try
        {
            using var conn = _db.Mangos();
            string tableName;
            int lootEntry;
            if (refEntry.HasValue && refEntry.Value > 0)
            {
                tableName = "reference_loot_template";
                lootEntry = refEntry.Value;
            }
            else
            {
                tableName = "creature_loot_template";
                var lootId = await conn.ExecuteScalarAsync<int?>(
                    "SELECT loot_id FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                    new { Entry = creatureEntry });
                if (lootId is null || lootId.Value == 0)
                    return McpResult.Failure(ErrorCodes.NotFound, "Creature has no loot table").ToJson();
                lootEntry = lootId.Value;
            }

            var exists = await conn.ExecuteScalarAsync<int>(
                $@"SELECT COUNT(*) FROM `{tableName}`
                   WHERE entry = @LootEntry AND item = @Item AND groupid = @GroupId",
                new { LootEntry = lootEntry, Item = itemEntry, GroupId = groupId });
            if (exists > 0)
                return McpResult.Failure(ErrorCodes.Conflict,
                    "Item already exists in this loot table (same group)").ToJson();

            await conn.ExecuteAsync(
                $@"INSERT INTO `{tableName}`
                        (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max, condition_id)
                    VALUES
                        (@LootEntry, @Item, @Chance, @GroupId, @MinCount, @MaxCount, @PatchMin, @PatchMax, 0)",
                new { LootEntry = lootEntry, Item = itemEntry, Chance = chance, GroupId = groupId,
                      MinCount = minCount, MaxCount = maxCount, PatchMin = patchMin, PatchMax = patchMax });

            var itemName = await conn.ExecuteScalarAsync<string>(
                "SELECT name FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = itemEntry }) ?? $"Item #{itemEntry}";
            var creatureName = await conn.ExecuteScalarAsync<string>(
                "SELECT name FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = creatureEntry }) ?? $"Creature #{creatureEntry}";

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "loot_add_item",
                TargetType = refEntry.HasValue ? "reference_loot" : "creature_loot",
                TargetName = $"{creatureName} → {itemName}",
                TargetId = creatureEntry,
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new
                {
                    table = tableName, lootEntry, item = itemEntry,
                    chance, groupId, minCount, maxCount
                }),
                IsReversible = true,
                Success = true,
                Notes = $"Added {itemName} (#{itemEntry}) to {tableName} entry={lootEntry} at {chance}%"
            });

            return McpResult.Success(new
            {
                success = true,
                itemName, creatureName, lootEntry, tableName,
                item = itemEntry, chance, groupId
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "instance_add_loot_item failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "instance_remove_loot_item")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Delete an item from a creature's loot table (or a reference_loot_template " +
        "pool if source='reference'). Captures before-state for audit.")]
    public async Task<string> RemoveLootItem(
        [Description("Loot table entry id.")] int entry,
        [Description("Item template entry id to remove.")] int item,
        [Description("Group id.")] int groupId,
        [Description("Patch min.")] int patchMin,
        [Description("Patch max.")] int patchMax,
        [Description("'direct' or 'reference'.")] string source = "direct")
    {
        var table = source == "reference" ? "reference_loot_template" : "creature_loot_template";
        try
        {
            using var conn = _db.Mangos();
            var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
                $@"SELECT * FROM `{table}`
                   WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                     AND patch_min = @PatchMin AND patch_max = @PatchMax",
                new { Entry = entry, Item = item, GroupId = groupId, PatchMin = patchMin, PatchMax = patchMax });
            if (before is null)
                return McpResult.Failure(ErrorCodes.NotFound, "Loot row not found").ToJson();

            var rows = await conn.ExecuteAsync(
                $@"DELETE FROM `{table}`
                   WHERE entry = @Entry AND item = @Item AND groupid = @GroupId
                     AND patch_min = @PatchMin AND patch_max = @PatchMax",
                new { Entry = entry, Item = item, GroupId = groupId, PatchMin = patchMin, PatchMax = patchMax });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "loot_remove_item",
                TargetType = "loot_row",
                TargetName = $"Item #{item}",
                TargetId = item,
                StateBefore = System.Text.Json.JsonSerializer.Serialize((IDictionary<string, object>)before),
                IsReversible = true,
                Success = true,
                Notes = $"Removed item #{item} from {table} entry={entry} ({rows} rows deleted)"
            });

            return McpResult.Success(new { success = true, deleted = rows }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "instance_remove_loot_item failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
