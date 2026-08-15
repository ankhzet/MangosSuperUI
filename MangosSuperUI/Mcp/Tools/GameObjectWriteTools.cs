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
/// Gameobject create/update/delete. All three require <c>write_db</c>.
/// Mirrors <c>GameObjectsController.Save/Delete</c>.
/// </summary>
[McpServerToolType]
public class GameObjectWriteTools
{
    private const int CustomRangeStart = 900000;

    private static readonly string[] EditableColumns = new[]
    {
        "name", "type", "displayId", "icon", "faction", "flags", "size",
        "data0", "data1", "data2", "data3", "data4", "data5", "data6", "data7",
        "data8", "data9", "data10", "data11", "data12", "data13", "data14", "data15",
        "data16", "data17", "data18", "data19", "data20", "data21", "data22", "data23"
    };

    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<GameObjectWriteTools> _log;

    public GameObjectWriteTools(ConnectionFactory db, AuditService audit,
        McpCallContext ctx, ILogger<GameObjectWriteTools> log)
    {
        _db = db;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "gameobject_create")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Insert a new custom gameobject (entry must be >= 900000). " +
        "`fields` is a map of EDITABLE_COLUMNS → values. " +
        "Columns: name, type, displayId, icon, faction, flags, size, " +
        "data0..data23. Numeric fields accept int/float; icon/name are strings. " +
        "Missing columns default to 0 / 'Custom Object' / 1.0.")]
    public async Task<string> Create(
        [Description("New entry id (must be >= 900000).")] int entry,
        [Description("Column → value map. Numeric values parsed as int (size as float).")] Dictionary<string, object>? fields = null)
    {
        if (entry < CustomRangeStart)
            return McpResult.Failure(ErrorCodes.InvalidInput, $"entry must be >= {CustomRangeStart}").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT entry FROM gameobject_template WHERE entry = @Entry LIMIT 1",
                new { Entry = entry });
            if (existing is not null)
                return McpResult.Failure(ErrorCodes.Conflict, $"entry {entry} already exists").ToJson();

            var p = BuildParameters(entry, fields);
            p.Add("Patch", 0);
            var columns = "entry, patch, " + string.Join(", ", EditableColumns);
            var values = "@Entry, @Patch, " + string.Join(", ", EditableColumns.Select(c => "@" + c));
            await conn.ExecuteAsync($"INSERT INTO gameobject_template ({columns}) VALUES ({values})", p);

            var after = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "gameobject_create",
                TargetType = "gameobject_custom",
                TargetName = fields?.TryGetValue("name", out var n) == true ? n?.ToString() ?? "Custom Object" : "Custom Object",
                TargetId = entry,
                StateAfter = after is null ? null : JsonSerializer.Serialize((IDictionary<string, object>)after),
                IsReversible = true,
                Success = true,
                Notes = $"Created custom game object #{entry}"
            });

            return McpResult.Success(new { success = true, entry, isInsert = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_create failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "gameobject_update")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Update an existing gameobject (custom or base). Updates the latest " +
        "patch row. `fields` keys must be EDITABLE_COLUMNS — anything else is ignored.")]
    public async Task<string> Update(
        [Description("Entry id to update.")] int entry,
        [Description("Column → value map to set.")] Dictionary<string, object> fields)
    {
        if (fields is null || fields.Count == 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "fields cannot be empty").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            if (before is null)
                return McpResult.Failure(ErrorCodes.NotFound, $"entry {entry} not found").ToJson();

            var patch = await conn.ExecuteScalarAsync<int>(
                "SELECT MAX(patch) FROM gameobject_template WHERE entry = @Entry",
                new { Entry = entry });
            var p = BuildParameters(entry, fields);
            p.Add("Patch", patch);
            var setClauses = string.Join(", ", EditableColumns.Select(c => $"{c} = @{c}"));
            await conn.ExecuteAsync(
                $"UPDATE gameobject_template SET {setClauses} WHERE entry = @Entry AND patch = @Patch", p);

            var after = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "gameobject_edit",
                TargetType = entry >= CustomRangeStart ? "gameobject_custom" : "gameobject_base_game",
                TargetName = fields.TryGetValue("name", out var n) ? n?.ToString() ?? "Unknown" : "Unknown",
                TargetId = entry,
                StateBefore = JsonSerializer.Serialize((IDictionary<string, object>)before),
                StateAfter = after is null ? null : JsonSerializer.Serialize((IDictionary<string, object>)after),
                IsReversible = true,
                Success = true,
                Notes = $"Edited game object #{entry}"
            });

            return McpResult.Success(new { success = true, entry, isInsert = false }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_update failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "gameobject_delete")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Delete a custom gameobject (entry >= 900000) and all of its spawns. " +
        "Refuses to delete vanilla base-game entries.")]
    public async Task<string> Delete(
        [Description("Entry id to delete (must be >= 900000).")] int entry)
    {
        if (entry < CustomRangeStart)
            return McpResult.Failure(ErrorCodes.InvalidInput,
                $"Cannot delete base game objects (entry < {CustomRangeStart})").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM gameobject_template WHERE entry = @Entry LIMIT 1",
                new { Entry = entry });
            if (before is null)
                return McpResult.Failure(ErrorCodes.NotFound, $"entry {entry} not found").ToJson();

            var spawnsDeleted = await conn.ExecuteAsync(
                "DELETE FROM gameobject WHERE id = @Id", new { Id = entry });
            var templatesDeleted = await conn.ExecuteAsync(
                "DELETE FROM gameobject_template WHERE entry = @Entry", new { Entry = entry });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "content",
                Action = "gameobject_delete",
                TargetType = "gameobject_custom",
                TargetName = (string?)before.name ?? $"Entry #{entry}",
                TargetId = entry,
                StateBefore = JsonSerializer.Serialize((IDictionary<string, object>)before),
                IsReversible = true,
                Success = true,
                Notes = $"Deleted custom game object #{entry} ({templatesDeleted} template rows, {spawnsDeleted} spawns)"
            });

            return McpResult.Success(new { success = true, templatesDeleted, spawnsDeleted }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_delete failed");
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
                    if (je.ValueKind == JsonValueKind.Number)
                        p.Add(col, col == "size" ? je.GetDouble() : je.GetInt32());
                    else if (je.ValueKind == JsonValueKind.String)
                        p.Add(col, je.GetString() ?? "");
                    else
                        p.Add(col, 0);
                }
                else
                {
                    p.Add(col, col == "size" ? Convert.ToDouble(raw) : Convert.ToInt32(raw));
                }
            }
            else
            {
                if (col == "name") p.Add(col, "Custom Object");
                else if (col == "icon") p.Add(col, "");
                else if (col == "size") p.Add(col, 1.0);
                else p.Add(col, 0);
            }
        }
        return p;
    }
}
