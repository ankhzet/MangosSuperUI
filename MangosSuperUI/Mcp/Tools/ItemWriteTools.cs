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
/// Item create/update/delete. All three require <c>write_db</c>. Mirrors
/// <c>ItemsController.Save/Delete</c>.
/// </summary>
[McpServerToolType]
public class ItemWriteTools
{
    private const int CustomRangeStart = 900000;

    private static readonly string[] EditableColumns = new[]
    {
        "name", "description", "class", "subclass", "quality", "display_id",
        "inventory_type", "flags",
        "required_level", "item_level", "required_skill", "required_skill_rank",
        "required_spell", "required_honor_rank", "required_city_rank",
        "required_reputation_faction", "required_reputation_rank",
        "allowable_class", "allowable_race",
        "buy_price", "sell_price", "buy_count", "bonding", "stackable", "max_count",
        "armor", "block", "holy_res", "fire_res", "nature_res", "frost_res", "shadow_res", "arcane_res",
        "dmg_min1", "dmg_max1", "dmg_type1", "dmg_min2", "dmg_max2", "dmg_type2",
        "dmg_min3", "dmg_max3", "dmg_type3", "dmg_min4", "dmg_max4", "dmg_type4",
        "dmg_min5", "dmg_max5", "dmg_type5",
        "delay", "range_mod", "ammo_type",
        "stat_type1", "stat_value1", "stat_type2", "stat_value2",
        "stat_type3", "stat_value3", "stat_type4", "stat_value4",
        "stat_type5", "stat_value5", "stat_type6", "stat_value6",
        "stat_type7", "stat_value7", "stat_type8", "stat_value8",
        "stat_type9", "stat_value9", "stat_type10", "stat_value10",
        "spellid_1", "spelltrigger_1", "spellcooldown_1", "spellcharges_1", "spellppmrate_1", "spellcategory_1", "spellcategorycooldown_1",
        "spellid_2", "spelltrigger_2", "spellcooldown_2", "spellcharges_2", "spellppmrate_2", "spellcategory_2", "spellcategorycooldown_2",
        "spellid_3", "spelltrigger_3", "spellcooldown_3", "spellcharges_3", "spellppmrate_3", "spellcategory_3", "spellcategorycooldown_3",
        "spellid_4", "spelltrigger_4", "spellcooldown_4", "spellcharges_4", "spellppmrate_4", "spellcategory_4", "spellcategorycooldown_4",
        "spellid_5", "spelltrigger_5", "spellcooldown_5", "spellcharges_5", "spellppmrate_5", "spellcategory_5", "spellcategorycooldown_5",
        "material", "sheath", "max_durability", "container_slots",
        "random_property", "set_id", "disenchant_id",
        "page_text", "page_language", "page_material",
        "start_quest", "lock_id",
        "area_bound", "map_bound", "duration", "bag_family",
        "food_type", "min_money_loot", "max_money_loot", "wrapped_gift",
        "extra_flags", "other_team_entry"
    };

    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<ItemWriteTools> _log;

    public ItemWriteTools(ConnectionFactory db, AuditService audit,
        McpCallContext ctx, ILogger<ItemWriteTools> log)
    {
        _db = db;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "item_create")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Insert a new item (entry must be >= 900000 for custom; lower values " +
        "create a vanilla-base-clone — be careful). `fields` is a map of " +
        "EDITABLE_COLUMNS → values. See ItemsController for the full column list; " +
        "missing columns default to 0 / 'Custom Item'.")]
    public async Task<string> Create(
        [Description("New entry id.")] int entry,
        [Description("Column → value map.")] Dictionary<string, object>? fields = null)
    {
        try
        {
            using var conn = _db.Mangos();
            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT entry FROM item_template WHERE entry = @Entry LIMIT 1",
                new { Entry = entry });
            if (existing is not null)
                return McpResult.Failure(ErrorCodes.Conflict, $"entry {entry} already exists").ToJson();

            var p = BuildParameters(entry, fields);
            p.Add("Patch", 0);
            var columns = "entry, patch, " + string.Join(", ", EditableColumns);
            var values = "@Entry, @Patch, " + string.Join(", ", EditableColumns.Select(c => "@" + c));
            await conn.ExecuteAsync($"INSERT INTO item_template ({columns}) VALUES ({values})", p);

            var after = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "item_create",
                TargetType = entry >= CustomRangeStart ? "item_custom" : "item_base_game",
                TargetName = fields?.TryGetValue("name", out var n) == true ? n?.ToString() ?? "Custom Item" : "Custom Item",
                TargetId = entry,
                StateAfter = after is null ? null : JsonSerializer.Serialize((IDictionary<string, object>)after),
                IsReversible = true,
                Success = true,
                Notes = $"Created item #{entry}"
            });

            return McpResult.Success(new { success = true, entry, isInsert = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_create failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "item_update")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Update an existing item (custom or base). Updates the latest patch " +
        "row. `fields` keys must be EDITABLE_COLUMNS — see ItemsController for " +
        "the full list.")]
    public async Task<string> Update(
        [Description("Entry id to update.")] int entry,
        [Description("Column → value map.")] Dictionary<string, object> fields)
    {
        if (fields is null || fields.Count == 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "fields cannot be empty").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            if (before is null)
                return McpResult.Failure(ErrorCodes.NotFound, $"entry {entry} not found").ToJson();

            var patch = await conn.ExecuteScalarAsync<int>(
                "SELECT MAX(patch) FROM item_template WHERE entry = @Entry",
                new { Entry = entry });
            var p = BuildParameters(entry, fields);
            p.Add("Patch", patch);
            var setClauses = string.Join(", ", EditableColumns.Select(c => $"{c} = @{c}"));
            await conn.ExecuteAsync(
                $"UPDATE item_template SET {setClauses} WHERE entry = @Entry AND patch = @Patch", p);

            var after = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "item_edit",
                TargetType = entry >= CustomRangeStart ? "item_custom" : "item_base_game",
                TargetName = fields.TryGetValue("name", out var n) ? n?.ToString() ?? "Unknown" : "Unknown",
                TargetId = entry,
                StateBefore = JsonSerializer.Serialize((IDictionary<string, object>)before),
                StateAfter = after is null ? null : JsonSerializer.Serialize((IDictionary<string, object>)after),
                IsReversible = true,
                Success = true,
                Notes = $"Edited item #{entry}"
            });

            return McpResult.Success(new { success = true, entry, isInsert = false }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_update failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "item_delete")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Delete an item (entry >= 900000 for custom; lower values refused " +
        "to protect vanilla data). Capture before-state for audit.")]
    public async Task<string> Delete(
        [Description("Entry id to delete (must be >= 900000).")] int entry)
    {
        if (entry < CustomRangeStart)
            return McpResult.Failure(ErrorCodes.InvalidInput,
                $"Cannot delete base game items (entry < {CustomRangeStart})").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry LIMIT 1",
                new { Entry = entry });
            if (before is null)
                return McpResult.Failure(ErrorCodes.NotFound, $"entry {entry} not found").ToJson();

            var rows = await conn.ExecuteAsync(
                "DELETE FROM item_template WHERE entry = @Entry", new { Entry = entry });

            var itemName = (string?)(before.name) ?? $"Entry #{entry}";
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "item_delete",
                TargetType = "item_custom",
                TargetName = itemName,
                TargetId = entry,
                StateBefore = JsonSerializer.Serialize((IDictionary<string, object>)before),
                IsReversible = false,
                Success = true,
                Notes = $"Deleted item #{entry} ({rows} rows)"
            });

            return McpResult.Success(new { success = true, deleted = rows }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "item_delete failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    private static DynamicParameters BuildParameters(int entry, Dictionary<string, object>? fields)
    {
        var p = new DynamicParameters();
        p.Add("Entry", entry);
        foreach (var col in EditableColumns)
        {
            if (fields is not null && fields.TryGetValue(col, out var raw) && raw is not null)
            {
                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number) p.Add(col, je.GetDouble());
                    else if (je.ValueKind == JsonValueKind.String) p.Add(col, je.GetString() ?? "");
                    else p.Add(col, 0);
                }
                else
                {
                    p.Add(col, Convert.ToDouble(raw));
                }
            }
            else
            {
                if (col == "name") p.Add(col, "Custom Item");
                else if (col == "description") p.Add(col, "");
                else p.Add(col, 0);
            }
        }
        return p;
    }
}
