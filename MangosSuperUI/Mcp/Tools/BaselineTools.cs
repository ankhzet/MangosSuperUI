using System.ComponentModel;
using System.Text.Json;
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
/// OG baseline (og_item_template, og_spell_template, og_creature_template, etc.)
/// reads + resets. Resets are destructive — gated by the <c>baseline</c>
/// capability.
///
/// Baseline = the snapshot of vanilla DBC at first init. Anything that
/// differs from baseline is a custom change (or a regression). Resetting
/// reverts one or many entries to baseline. Audit log + change graph
/// capture before/after state.
/// </summary>
[McpServerToolType]
public class BaselineTools
{
    private static readonly string[] OgTables = new[]
    {
        "og_item_template", "og_spell_template", "og_gameobject_template",
        "og_creature_template", "og_creature_loot_template",
        "og_reference_loot_template", "og_disenchant_loot_template",
        "og_pickpocketing_loot_template", "og_skinning_loot_template",
        "og_npc_trainer", "og_npc_trainer_template", "og_skill_line_ability"
    };

    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<BaselineTools> _log;

    public BaselineTools(ConnectionFactory db, AuditService audit,
        McpCallContext ctx, ILogger<BaselineTools> log)
    {
        _db = db;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "baseline_status")]
    [Description(
        "OG baseline readiness: which og_* tables exist in the admin DB, " +
        "their row counts, and any that returned errors. Use before " +
        "baseline_initialize if unsure whether the baseline was ever seeded.")]
    public async Task<string> Status()
    {
        try
        {
            using var admin = _db.Admin();
            var tables = new List<object>();
            foreach (var t in OgTables)
            {
                try
                {
                    var exists = await admin.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t",
                        new { t });
                    var rows = exists > 0 ? await admin.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{t}`") : 0;
                    tables.Add(new { table = t, exists = exists > 0, rowCount = rows });
                }
                catch (Exception ex)
                {
                    tables.Add(new { table = t, exists = false, error = ex.Message });
                }
            }
            return McpResult.Success(new { tables }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_status failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_initialize")]
    [McpCapability(McpCapability.Baseline)]
    [Description(
        "Create og_* snapshot tables from the current mangos state and copy " +
        "every row. Idempotent — running twice leaves the baseline unchanged. " +
        "Run once after the world DB is fully loaded.")]
    public async Task<string> Initialize()
    {
        try
        {
            using var admin = _db.Admin();
            using var mangos = _db.Mangos();
            var results = new List<object>();
            var pairs = new (string Source, string Og)[]
            {
                ("item_template", "og_item_template"),
                ("spell_template", "og_spell_template"),
                ("gameobject_template", "og_gameobject_template"),
                ("creature_template", "og_creature_template"),
                ("creature_loot_template", "og_creature_loot_template"),
                ("reference_loot_template", "og_reference_loot_template"),
                ("disenchant_loot_template", "og_disenchant_loot_template"),
                ("pickpocketing_loot_template", "og_pickpocketing_loot_template"),
                ("skinning_loot_template", "og_skinning_loot_template"),
                ("npc_trainer", "og_npc_trainer"),
                ("npc_trainer_template", "og_npc_trainer_template"),
                ("skill_line_ability", "og_skill_line_ability")
            };
            foreach (var (src, og) in pairs)
            {
                try
                {
                    var exists = await admin.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @og",
                        new { og });
                    if (exists == 0)
                    {
                        // Create table by copying structure + data from src
                        await admin.ExecuteAsync($"CREATE TABLE `{og}` LIKE `{src}`");
                        await admin.ExecuteAsync($"INSERT INTO `{og}` SELECT * FROM `{src}`");
                    }
                    var rows = await admin.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{og}`");
                    results.Add(new { table = og, status = "ok", rowCount = rows });
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed baseline table {Table}", og);
                    results.Add(new { table = og, status = "error", error = ex.Message });
                }
            }
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "system",
                Action = "baseline_initialize",
                TargetType = "baseline",
                TargetName = "og_baseline",
                StateAfter = JsonSerializer.Serialize(results),
                Success = true,
                Notes = $"Initialized OG baseline tables ({results.Count} tables)"
            });
            return McpResult.Success(new { success = true, tables = results }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_initialize failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_diff_item")]
    [Description(
        "Field-level diff of item_template vs og_item_template for one entry. " +
        "Returns {available:false} if baseline not initialized, " +
        "{isCustom:true} for entries >= 900000 (no baseline to compare).")]
    public async Task<string> DiffItem(
        [Description("Item entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var admin = _db.Admin();
            if (!await TableExists(admin, "og_item_template"))
                return McpResult.Success(new { available = false, reason = "Baseline not initialized" }).ToJson();
            var og = await admin.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM og_item_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1",
                new { entry });
            if (og is null)
                return McpResult.Success(new { available = true, isCustom = entry >= 900000, hasOriginal = false, changes = Array.Empty<object>() }).ToJson();
            using var mangos = _db.Mangos();
            var cur = await mangos.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1",
                new { entry });
            if (cur is null)
                return McpResult.Success(new { available = true, hasOriginal = true, deleted = true }).ToJson();
            var changes = BuildDiff((IDictionary<string, object>)og, (IDictionary<string, object>)cur);
            return McpResult.Success(new { available = true, hasOriginal = true, isCustom = false, isModified = changes.Count > 0, changes }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_diff_item failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_diff_spell")]
    [Description("Field-level diff of spell_template vs og_spell_template for one entry.")]
    public async Task<string> DiffSpell(
        [Description("Spell entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var admin = _db.Admin();
            if (!await TableExists(admin, "og_spell_template"))
                return McpResult.Success(new { available = false, reason = "Baseline not initialized" }).ToJson();
            var og = await admin.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM og_spell_template WHERE entry = @entry ORDER BY build DESC LIMIT 1",
                new { entry });
            if (og is null)
                return McpResult.Success(new { available = true, isCustom = entry >= 40000, hasOriginal = false }).ToJson();
            using var mangos = _db.Mangos();
            var cur = await mangos.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM spell_template WHERE entry = @entry ORDER BY build DESC LIMIT 1",
                new { entry });
            if (cur is null)
                return McpResult.Success(new { available = true, hasOriginal = true, deleted = true }).ToJson();
            var changes = BuildDiff((IDictionary<string, object>)og, (IDictionary<string, object>)cur);
            return McpResult.Success(new { available = true, hasOriginal = true, isCustom = false, isModified = changes.Count > 0, changes }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_diff_spell failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_diff_gameobject")]
    [Description("Field-level diff of gameobject_template vs og_gameobject_template for one entry.")]
    public async Task<string> DiffGameObject(
        [Description("Gameobject entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var admin = _db.Admin();
            if (!await TableExists(admin, "og_gameobject_template"))
                return McpResult.Success(new { available = false, reason = "Baseline not initialized" }).ToJson();
            var og = await admin.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM og_gameobject_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1",
                new { entry });
            if (og is null)
                return McpResult.Success(new { available = true, isCustom = entry >= 900000, hasOriginal = false }).ToJson();
            using var mangos = _db.Mangos();
            var cur = await mangos.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM gameobject_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1",
                new { entry });
            if (cur is null)
                return McpResult.Success(new { available = true, hasOriginal = true, deleted = true }).ToJson();
            var changes = BuildDiff((IDictionary<string, object>)og, (IDictionary<string, object>)cur);
            return McpResult.Success(new { available = true, hasOriginal = true, isCustom = false, isModified = changes.Count > 0, changes }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_diff_gameobject failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_diff_creature_loot")]
    [Description(
        "Diff one creature's direct + ref loot tables against the baseline " +
        "(og_creature_loot_template / og_reference_loot_template).")]
    public async Task<string> DiffCreatureLoot(
        [Description("Creature entry id.")] int creatureEntry)
    {
        if (creatureEntry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "creatureEntry must be positive").ToJson();
        try
        {
            using var mangos = _db.Mangos();
            var ct = await mangos.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT loot_id FROM creature_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1",
                new { entry = creatureEntry });
            if (ct is null)
                return McpResult.Failure(ErrorCodes.NotFound, "creature not found").ToJson();
            int lootId = (int)ct.loot_id;
            using var admin = _db.Admin();
            bool ogExists = await TableExists(admin, "og_creature_loot_template");
            if (!ogExists)
                return McpResult.Success(new { available = false, reason = "Baseline not initialized" }).ToJson();
            var ogDirect = (await admin.QueryAsync<dynamic>(
                "SELECT * FROM og_creature_loot_template WHERE entry = @lid", new { lid = lootId })).ToList();
            var curDirect = (await mangos.QueryAsync<dynamic>(
                "SELECT * FROM creature_loot_template WHERE entry = @lid", new { lid = lootId })).ToList();
            return McpResult.Success(new
            {
                available = true,
                creatureEntry,
                lootId,
                ogDirectCount = ogDirect.Count,
                curDirectCount = curDirect.Count,
                directModified = ogDirect.Count != curDirect.Count
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_diff_creature_loot failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_diff_loot")]
    [Description(
        "Diff one loot table entry between current and baseline for the " +
        "given table ('creature_loot_template', 'reference_loot_template', " +
        "'disenchant_loot_template', 'pickpocketing_loot_template', " +
        "'skinning_loot_template').")]
    public async Task<string> DiffLoot(
        [Description("Loot table name (without prefix). E.g. 'creature_loot_template' or just 'creature_loot'.")] string table,
        [Description("Loot table entry id.")] int entry)
    {
        if (entry <= 0 || string.IsNullOrWhiteSpace(table))
            return McpResult.Failure(ErrorCodes.InvalidInput, "table and entry required").ToJson();
        var currentTable = table.StartsWith("og_") ? table : table;
        var ogTable = currentTable.StartsWith("og_") ? currentTable : "og_" + currentTable;
        try
        {
            using var admin = _db.Admin();
            if (!await TableExists(admin, ogTable))
                return McpResult.Success(new { available = false, reason = "Baseline not initialized" }).ToJson();
            var og = await admin.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{ogTable}` WHERE entry = @entry", new { entry });
            using var mangos = _db.Mangos();
            var cur = await mangos.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{currentTable}` WHERE entry = @entry", new { entry });
            return McpResult.Success(new
            {
                available = true,
                table = currentTable,
                entry,
                ogRowCount = og,
                curRowCount = cur,
                delta = cur - og
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_diff_loot failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_reset_item")]
    [McpCapability(McpCapability.Baseline)]
    [Description(
        "Revert one item_template row to its og_item_template state. " +
        "Destructive — overwrites the current row.")]
    public async Task<string> ResetItem(
        [Description("Item entry id.")] int entry)
    {
        if (entry <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            return await ResetTemplateAsync("item_template", "og_item_template", entry,
                keyCol: "entry", beforeNotes: $"Reset item #{entry} from baseline");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_reset_item failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_reset_spell")]
    [McpCapability(McpCapability.Baseline)]
    [Description("Revert one spell_template row to its og_spell_template state.")]
    public async Task<string> ResetSpell(
        [Description("Spell entry id.")] int entry)
    {
        if (entry <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            return await ResetTemplateAsync("spell_template", "og_spell_template", entry,
                keyCol: "entry", beforeNotes: $"Reset spell #{entry} from baseline");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_reset_spell failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_reset_gameobject")]
    [McpCapability(McpCapability.Baseline)]
    [Description("Revert one gameobject_template row to its og_gameobject_template state.")]
    public async Task<string> ResetGameObject(
        [Description("Gameobject entry id.")] int entry)
    {
        if (entry <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            return await ResetTemplateAsync("gameobject_template", "og_gameobject_template", entry,
                keyCol: "entry", beforeNotes: $"Reset gameobject #{entry} from baseline");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_reset_gameobject failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_reset_creature_loot")]
    [McpCapability(McpCapability.Baseline)]
    [Description(
        "Restore all loot (direct + reference tables) for one creature to " +
        "the baseline state.")]
    public async Task<string> ResetCreatureLoot(
        [Description("Creature entry id.")] int creatureEntry)
    {
        if (creatureEntry <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "creatureEntry must be positive").ToJson();
        try
        {
            using var mangos = _db.Mangos();
            var lootId = await mangos.ExecuteScalarAsync<int?>(
                "SELECT loot_id FROM creature_template WHERE entry = @entry ORDER BY patch DESC LIMIT 1",
                new { entry = creatureEntry });
            if (lootId is null || lootId.Value == 0)
                return McpResult.Failure(ErrorCodes.NotFound, "creature has no loot_id").ToJson();
            using var admin = _db.Admin();
            // Delete current rows
            var deleted1 = await mangos.ExecuteAsync("DELETE FROM creature_loot_template WHERE entry = @lid", new { lid = lootId.Value });
            // Restore from og
            var restored1 = await mangos.ExecuteAsync(
                "INSERT INTO creature_loot_template SELECT * FROM og_creature_loot_template WHERE entry = @lid",
                new { lid = lootId.Value });
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator, OperatorIp = _ctx.RemoteIp,
                Category = "baseline", Action = "reset_creature_loot",
                TargetType = "creature", TargetName = $"#{creatureEntry}",
                TargetId = creatureEntry,
                StateAfter = JsonSerializer.Serialize(new { lootId = lootId.Value, deleted = deleted1, restored = restored1 }),
                IsReversible = false, Success = true,
                Notes = $"Reset loot for creature #{creatureEntry} from baseline"
            });
            return McpResult.Success(new { success = true, lootId = lootId.Value, deleted = deleted1, restored = restored1 }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_reset_creature_loot failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_reset_table")]
    [McpCapability(McpCapability.Baseline)]
    [Description(
        "Reset every row of one loot table to its og_* state. Nuclear option " +
        "for a single table — wipes all custom loot across the entire world.")]
    public async Task<string> ResetTable(
        [Description("Loot table name (without og_ prefix). E.g. 'creature_loot_template'.")] string table)
    {
        if (string.IsNullOrWhiteSpace(table))
            return McpResult.Failure(ErrorCodes.InvalidInput, "table required").ToJson();
        var currentTable = table.StartsWith("og_") ? table.Substring(3) : table;
        var ogTable = "og_" + currentTable;
        try
        {
            using var mangos = _db.Mangos();
            using var admin = _db.Admin();
            if (!await TableExists(admin, ogTable))
                return McpResult.Failure(ErrorCodes.NotFound, $"{ogTable} doesn't exist; initialize baseline first").ToJson();
            var deleted = await mangos.ExecuteAsync($"DELETE FROM `{currentTable}`");
            var restored = await mangos.ExecuteAsync($"INSERT INTO `{currentTable}` SELECT * FROM `{ogTable}`");
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator, OperatorIp = _ctx.RemoteIp,
                Category = "baseline", Action = "reset_table",
                TargetType = "table", TargetName = currentTable,
                StateAfter = JsonSerializer.Serialize(new { deleted, restored }),
                IsReversible = false, Success = true,
                Notes = $"Reset table {currentTable} from baseline ({restored} rows restored)"
            });
            return McpResult.Success(new { success = true, table = currentTable, deleted, restored }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_reset_table failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "baseline_reset_all")]
    [McpCapability(McpCapability.Baseline)]
    [Description(
        "NUCLEAR OPTION: restore every og_* snapshot table. Wipes ALL custom " +
        "items, spells, gameobjects, loot, trainer wiring across the whole world. " +
        "Audit log gets a giant state_after entry.")]
    public async Task<string> ResetAll()
    {
        try
        {
            using var mangos = _db.Mangos();
            using var admin = _db.Admin();
            var results = new List<object>();
            foreach (var (src, og) in new (string, string)[]
            {
                ("item_template", "og_item_template"),
                ("spell_template", "og_spell_template"),
                ("gameobject_template", "og_gameobject_template"),
                ("creature_template", "og_creature_template"),
                ("creature_loot_template", "og_creature_loot_template"),
                ("reference_loot_template", "og_reference_loot_template"),
                ("disenchant_loot_template", "og_disenchant_loot_template"),
                ("pickpocketing_loot_template", "og_pickpocketing_loot_template"),
                ("skinning_loot_template", "og_skinning_loot_template"),
                ("npc_trainer", "og_npc_trainer"),
                ("npc_trainer_template", "og_npc_trainer_template"),
                ("skill_line_ability", "og_skill_line_ability")
            })
            {
                try
                {
                    var exists = await admin.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @og",
                        new { og });
                    if (exists == 0) { results.Add(new { table = src, status = "skipped (no baseline)" }); continue; }
                    var deleted = await mangos.ExecuteAsync($"DELETE FROM `{src}`");
                    var restored = await mangos.ExecuteAsync($"INSERT INTO `{src}` SELECT * FROM `{og}`");
                    results.Add(new { table = src, deleted, restored });
                }
                catch (Exception ex)
                {
                    results.Add(new { table = src, status = "error", error = ex.Message });
                }
            }
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator, OperatorIp = _ctx.RemoteIp,
                Category = "baseline", Action = "reset_all",
                TargetType = "baseline", TargetName = "og_baseline",
                StateAfter = JsonSerializer.Serialize(results),
                IsReversible = false, Success = true,
                Notes = "NUCLEAR: reset every og_* snapshot table"
            });
            return McpResult.Success(new { success = true, results }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "baseline_reset_all failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    // ----- helpers -----

    private async Task<string> ResetTemplateAsync(string currentTable, string ogTable, int entry, string keyCol, string beforeNotes)
    {
        using var admin = _db.Admin();
        if (!await TableExists(admin, ogTable))
            return McpResult.Failure(ErrorCodes.NotFound, $"{ogTable} doesn't exist; initialize baseline first").ToJson();
        var og = await admin.QueryFirstOrDefaultAsync<dynamic>(
            $"SELECT * FROM `{ogTable}` WHERE {keyCol} = @entry ORDER BY patch DESC LIMIT 1",
            new { entry });
        if (og is null)
            return McpResult.Failure(ErrorCodes.NotFound, $"no baseline row for {keyCol}={entry}").ToJson();
        using var mangos = _db.Mangos();
        var before = await mangos.QueryFirstOrDefaultAsync<dynamic>(
            $"SELECT * FROM `{currentTable}` WHERE {keyCol} = @entry ORDER BY patch DESC LIMIT 1",
            new { entry });
        var deleted = await mangos.ExecuteAsync($"DELETE FROM `{currentTable}` WHERE {keyCol} = @entry", new { entry });
        var restored = await mangos.ExecuteAsync(
            $"INSERT INTO `{currentTable}` SELECT * FROM `{ogTable}` WHERE {keyCol} = @entry",
            new { entry });
        await _audit.LogAsync(new AuditEntry
        {
            Operator = _ctx.Operator, OperatorIp = _ctx.RemoteIp,
            Category = "baseline", Action = "reset_template",
            TargetType = currentTable, TargetName = $"{keyCol}={entry}",
            StateBefore = before is null ? null : JsonSerializer.Serialize((IDictionary<string, object>)before),
            StateAfter = JsonSerializer.Serialize((IDictionary<string, object>)og),
            IsReversible = false, Success = true,
            Notes = beforeNotes + $" ({restored} row)"
        });
        return McpResult.Success(new { success = true, deleted, restored }).ToJson();
    }

    private static async Task<bool> TableExists(MySqlConnector.MySqlConnection conn, string table)
    {
        var n = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t",
            new { t = table });
        return n > 0;
    }

    private static List<object> BuildDiff(IDictionary<string, object> og, IDictionary<string, object> cur)
    {
        var diff = new List<object>();
        foreach (var k in og.Keys)
        {
            if (cur.TryGetValue(k, out var curVal))
            {
                var ogVal = og[k];
                if (!Equals(ogVal, curVal))
                {
                    diff.Add(new { field = k, from = ogVal?.ToString(), to = curVal?.ToString() });
                }
            }
        }
        return diff;
    }
}
