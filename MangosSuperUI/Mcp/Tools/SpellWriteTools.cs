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
/// Spell search/detail + save + trainer wiring + character teach/unlearn +
/// spell deletion. Reads default to <c>read</c>; writes need
/// <c>write_db</c> + <c>patches</c> (trainer wiring, deletion).
/// </summary>
[McpServerToolType]
public class SpellWriteTools
{
    private readonly ConnectionFactory _db;
    private readonly SpellCreatorService _spells;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<SpellWriteTools> _log;

    public SpellWriteTools(ConnectionFactory db, SpellCreatorService spells,
        AuditService audit, McpCallContext ctx, ILogger<SpellWriteTools> log)
    {
        _db = db;
        _spells = spells;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    // ===================== READS =====================

    [McpServerTool(Name = "spell_search")]
    [Description(
        "Paginated spell_template search. Numeric `query` matches entry id; " +
        "non-numeric matches name substring. Optional school and mechanic filters.")]
    public async Task<string> Search(
        [Description("Search term (entry id or name substring).")] string? query = null,
        [Description("School id (0=holy, 1=fire, 2=nature, 3=frost, 4=shadow, 5=arcane).")] int? schoolFilter = null,
        [Description("Mechanic id.")] int? mechanicFilter = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 100.")] int pageSize = 50)
    {
        var capped = Math.Clamp(pageSize, 1, 100);
        try
        {
            using var conn = _db.Mangos();
            var where = "WHERE patch = (SELECT MAX(patch) FROM spell_template st2 WHERE st2.entry = spell_template.entry)";
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
            if (schoolFilter.HasValue) { where += " AND school = @School"; p.Add("School", schoolFilter.Value); }
            if (mechanicFilter.HasValue) { where += " AND mechanic = @Mech"; p.Add("Mech", mechanicFilter.Value); }

            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM spell_template {where}", p);
            p.Add("Offset", (Math.Max(1, page) - 1) * capped);
            p.Add("PageSize", capped);
            var rows = (await conn.QueryAsync<dynamic>(
                $@"SELECT entry, name, school, mechanic, casttime, duration, range, level
                   FROM spell_template {where}
                   ORDER BY entry LIMIT @PageSize OFFSET @Offset", p)).ToList();
            return McpResult.Success(new
            {
                total, page, pageSize = capped,
                totalPages = (int)Math.Ceiling((double)total / capped),
                spells = rows
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "spell_search failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "spell_detail")]
    [Description("Full spell_template record by entry id (latest build/patch).")]
    public async Task<string> Detail(
        [Description("Spell entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM spell_template WHERE entry = @entry ORDER BY build DESC LIMIT 1",
                new { entry });
            return McpResult.Success(row is null ? new { found = false, entry } : new { found = true, entry, spell = row }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "spell_detail failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    // ===================== WRITES =====================

    [McpServerTool(Name = "spell_save")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Edit one spell. `changes` is a map of column → value. Columns " +
        "immutable to editing (entry, build) are ignored. Server restart " +
        "required after the edit.")]
    public async Task<string> Save(
        [Description("Spell entry id.")] int entry,
        [Description("Column → value map.")] Dictionary<string, object?> changes)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        if (changes is null || changes.Count == 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "no changes provided").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var current = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM spell_template WHERE entry = @entry ORDER BY build DESC LIMIT 1",
                new { entry });
            if (current is null)
                return McpResult.Failure(ErrorCodes.NotFound, $"Spell {entry} not found").ToJson();

            var currentDict = (IDictionary<string, object>)current;
            var build = Convert.ToInt32(currentDict["build"] ?? 0);
            var immutable = new HashSet<string> { "entry", "build" };
            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            var beforeState = new Dictionary<string, object?>();
            var afterState = new Dictionary<string, object?>();

            foreach (var (col, raw) in changes)
            {
                if (immutable.Contains(col)) continue;
                if (!currentDict.ContainsKey(col)) continue;
                beforeState[col] = currentDict[col];
                afterState[col] = raw;
                setClauses.Add($"`{col}` = @p_{col}");
                if (raw is JsonElement je)
                {
                    switch (je.ValueKind)
                    {
                        case JsonValueKind.Number:
                            if (je.TryGetInt64(out var lv)) parameters.Add($"p_{col}", lv);
                            else if (je.TryGetDouble(out var dv)) parameters.Add($"p_{col}", dv);
                            else parameters.Add($"p_{col}", je.GetRawText());
                            break;
                        case JsonValueKind.String: parameters.Add($"p_{col}", je.GetString()); break;
                        case JsonValueKind.Null: parameters.Add($"p_{col}", (object?)null); break;
                        default: parameters.Add($"p_{col}", je.GetRawText()); break;
                    }
                }
                else
                {
                    parameters.Add($"p_{col}", raw);
                }
            }

            if (setClauses.Count == 0)
                return McpResult.Failure(ErrorCodes.InvalidInput, "no valid changes after filtering").ToJson();

            parameters.Add("entry", entry);
            parameters.Add("build", build);
            var affected = await conn.ExecuteAsync(
                $"UPDATE spell_template SET {string.Join(", ", setClauses)} WHERE entry = @entry AND build = @build",
                parameters);

            var spellName = currentDict.TryGetValue("name", out var n) ? n?.ToString() : $"Spell #{entry}";
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "spell_edit",
                TargetType = "spell_template",
                TargetName = $"{spellName} (#{entry})",
                TargetId = entry,
                StateBefore = JsonSerializer.Serialize(beforeState),
                StateAfter = JsonSerializer.Serialize(afterState),
                IsReversible = true,
                Success = affected > 0,
                Notes = $"Edited {setClauses.Count} field(s) on spell #{entry}. Server restart required."
            });

            return McpResult.Success(new { success = affected > 0, affected, fields = setClauses.Count }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "spell_save failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "spell_save_batch")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Apply the same `changes` to multiple spell entries. Same column " +
        "whitelist as spell_save. Use for bulk rebalancing (e.g. 12 warlock " +
        "dots sharing one DamageMultiplier change).")]
    public async Task<string> SaveBatch(
        [Description("Spell entry ids.")] int[] entries,
        [Description("Column → value map.")] Dictionary<string, object?> changes)
    {
        if (entries is null || entries.Length == 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entries cannot be empty").ToJson();
        if (changes is null || changes.Count == 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "no changes provided").ToJson();

        var results = new List<object>();
        foreach (var e in entries)
        {
            var r = await Save(e, changes);
            results.Add(new { entry = e, result = r });
        }
        return McpResult.Success(new { count = results.Count, results }).ToJson();
    }

    [McpServerTool(Name = "patch_teach_spell")]
    [McpCapability(McpCapability.WriteDb)]
    [Description("Teach a spell to a character. Inserts into `character_spell`. Server restart required.")]
    public async Task<string> TeachSpell(
        [Description("Spell entry id.")] int spellEntry,
        [Description("Character guid.")] int characterGuid)
    {
        if (spellEntry <= 0 || characterGuid <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "spellEntry and characterGuid must be positive").ToJson();
        try
        {
            var ok = await _spells.TeachSpellToCharacterAsync(spellEntry, characterGuid, _ctx.RemoteIp);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "spell_teach",
                TargetType = "character",
                TargetName = $"Character #{characterGuid}",
                TargetId = characterGuid,
                StateAfter = JsonSerializer.Serialize(new { spellEntry }),
                IsReversible = true,
                Success = ok,
                Notes = $"Taught spell #{spellEntry} to character #{characterGuid}"
            });
            return McpResult.Success(new { success = ok }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_teach_spell failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_unlearn_spell")]
    [McpCapability(McpCapability.WriteDb)]
    [Description("Unlearn a spell from a character. Deletes from `character_spell`. Server restart required.")]
    public async Task<string> UnlearnSpell(
        [Description("Spell entry id.")] int spellEntry,
        [Description("Character guid.")] int characterGuid)
    {
        if (spellEntry <= 0 || characterGuid <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "spellEntry and characterGuid must be positive").ToJson();
        try
        {
            var ok = await _spells.UnlearnSpellFromCharacterAsync(spellEntry, characterGuid, _ctx.RemoteIp);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "spell_unlearn",
                TargetType = "character",
                TargetName = $"Character #{characterGuid}",
                TargetId = characterGuid,
                StateAfter = JsonSerializer.Serialize(new { spellEntry }),
                IsReversible = true,
                Success = ok,
                Notes = $"Unlearned spell #{spellEntry} from character #{characterGuid}"
            });
            return McpResult.Success(new { success = ok }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_unlearn_spell failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_register_at_trainer")]
    [McpCapability(McpCapability.Patches)]
    [Description(
        "Register a custom spell at one trainer NPC (npc_trainer row). " +
        "Spell must be in the custom 40000+ range. Server restart required.")]
    public async Task<string> RegisterAtTrainer(
        [Description("Custom spell entry id (40000+).")] int spellEntry,
        [Description("Trainer NPC entry id.")] int trainerEntry,
        [Description("Training cost in copper.")] int cost,
        [Description("Required player level to learn.")] int reqLevel)
    {
        if (spellEntry <= 0 || trainerEntry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "spellEntry and trainerEntry must be positive").ToJson();
        try
        {
            var ok = await _spells.InsertNpcTrainerAsync(trainerEntry, spellEntry, cost, reqLevel);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "patch",
                Action = "spell_register_trainer",
                TargetType = "npc_trainer",
                TargetName = $"Trainer #{trainerEntry}",
                TargetId = trainerEntry,
                StateAfter = JsonSerializer.Serialize(new { spellEntry, cost, reqLevel }),
                IsReversible = true,
                Success = ok,
                Notes = $"Registered spell #{spellEntry} at trainer #{trainerEntry}"
            });
            return McpResult.Success(new { success = ok }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_register_at_trainer failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_register_at_class_trainers")]
    [McpCapability(McpCapability.Patches)]
    [Description(
        "Register a custom spell at every class trainer (every npc_trainer_template " +
        "for the given class). Spell must be in the custom 40000+ range.")]
    public async Task<string> RegisterAtClassTrainers(
        [Description("Custom spell entry id.")] int spellEntry,
        [Description("Class id (1=warrior, 2=paladin, ...).")] int trainerClass,
        [Description("Training cost in copper.")] int cost,
        [Description("Required player level to learn.")] int reqLevel)
    {
        if (spellEntry <= 0 || trainerClass <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "spellEntry and trainerClass must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var templateIds = (await conn.QueryAsync<int>(
                "SELECT DISTINCT templateid FROM npc_trainer_template")).ToList();
            int inserted = 0;
            foreach (var tid in templateIds)
            {
                if (await _spells.InsertNpcTrainerTemplateAsync(tid, spellEntry, cost, reqLevel))
                    inserted++;
            }
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "patch",
                Action = "spell_register_class_trainers",
                TargetType = "npc_trainer_template",
                TargetName = $"Class {trainerClass}",
                TargetId = trainerClass,
                StateAfter = JsonSerializer.Serialize(new { spellEntry, cost, reqLevel, templateCount = templateIds.Count, inserted }),
                IsReversible = true,
                Success = inserted > 0,
                Notes = $"Registered spell #{spellEntry} at {inserted}/{templateIds.Count} trainer templates"
            });
            return McpResult.Success(new { success = inserted > 0, inserted, templateCount = templateIds.Count }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_register_at_class_trainers failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_copy_source_trainers")]
    [McpCapability(McpCapability.Patches)]
    [Description(
        "Copy the trainer wiring from a vanilla source spell to a custom spell. " +
        "Walks wrapper spells (SPELL_EFFECT_LEARN_SPELL) and copies their " +
        "npc_trainer entries. Cost + reqLevel are forced.")]
    public async Task<string> CopySourceTrainers(
        [Description("New custom spell entry id.")] int spellEntry,
        [Description("Vanilla source spell entry id.")] int sourceSpellEntry,
        [Description("Training cost in copper (forced).")] int cost,
        [Description("Required player level (forced).")] int reqLevel)
    {
        if (spellEntry <= 0 || sourceSpellEntry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "spellEntry and sourceSpellEntry must be positive").ToJson();
        try
        {
            var total = await _spells.CopyTrainerEntriesFromSourceAsync(sourceSpellEntry, spellEntry, cost, reqLevel);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "patch",
                Action = "spell_copy_source_trainers",
                TargetType = "npc_trainer",
                TargetName = $"Spell #{spellEntry}",
                TargetId = spellEntry,
                StateAfter = JsonSerializer.Serialize(new { sourceSpellEntry, cost, reqLevel, total }),
                IsReversible = true,
                Success = total > 0,
                Notes = $"Copied {total} trainer entries from spell #{sourceSpellEntry} to spell #{spellEntry}"
            });
            return McpResult.Success(new { success = total > 0, copied = total }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_copy_source_trainers failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_delete_spell")]
    [McpCapability(McpCapability.Patches)]
    [Description(
        "Delete a custom spell (cascades to skill_line_ability, spell_chain, " +
        "npc_trainer, custom_spell_meta). Spell must be in the 40000-49999 range. " +
        "Server restart required.")]
    public async Task<string> DeleteSpell(
        [Description("Custom spell entry id (40000-49999).")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            var ok = await _spells.DeleteCustomSpellAsync(entry, _ctx.RemoteIp);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "patch",
                Action = "spell_delete",
                TargetType = "spell_custom",
                TargetName = $"Spell #{entry}",
                TargetId = entry,
                IsReversible = false,
                Success = ok,
                Notes = $"Deleted custom spell #{entry}"
            });
            return McpResult.Success(new { success = ok }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_delete_spell failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
