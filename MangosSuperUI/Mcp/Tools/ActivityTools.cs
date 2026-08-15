using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Activity log reads — the dashboard's "what changed" view. Wraps
/// <c>ActivityController</c>.
/// </summary>
[McpServerToolType]
public class ActivityTools
{
    private readonly AuditService _audit;
    private readonly ConnectionFactory _db;
    private readonly ILogger<ActivityTools> _log;

    public ActivityTools(AuditService audit, ConnectionFactory db, ILogger<ActivityTools> log)
    {
        _audit = audit;
        _db = db;
        _log = log;
    }

    [McpServerTool(Name = "activity_entries")]
    [Description(
        "Paginated audit log entries. Optional filters: category, search " +
        "substring (matches action/target_name), and `successOnly` (true = only " +
        "successful actions).")]
    public async Task<string> Entries(
        [Description("Optional category filter.")] string? category = null,
        [Description("Optional substring filter.")] string? search = null,
        [Description("Show only successful actions.")] bool? successOnly = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Admin();
            var where = new List<string> { "1=1" };
            var p = new DynamicParameters();
            if (!string.IsNullOrWhiteSpace(category)) { where.Add("category = @category"); p.Add("category", category); }
            if (!string.IsNullOrWhiteSpace(search))
            {
                where.Add("(action LIKE @search OR target_name LIKE @search)");
                p.Add("search", $"%{search}%");
            }
            if (successOnly == true) where.Add("success = 1");

            var whereClause = "WHERE " + string.Join(" AND ", where);
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM audit_log {whereClause}", p);
            p.Add("limit", Math.Clamp(pageSize, 1, 200));
            p.Add("offset", (Math.Max(1, page) - 1) * pageSize);

            var rows = (await conn.QueryAsync<dynamic>($@"
                SELECT id, `timestamp`, operator, operator_ip AS operatorIp,
                       category, action, target_type AS targetType,
                       target_name AS targetName, target_id AS targetId,
                       ra_command AS raCommand, success, notes
                FROM audit_log {whereClause}
                ORDER BY `timestamp` DESC LIMIT @limit OFFSET @offset", p)).ToList();

            return McpResult.Success(new
            {
                total, page, pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize),
                entries = rows
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "activity_entries failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "activity_summary")]
    [Description(
        "Activity Log summary cards: per-category counts, total, recent failures, " +
        "today's count.")]
    public async Task<string> Summary()
    {
        try
        {
            using var conn = _db.Admin();
            var byCategory = (await conn.QueryAsync<(string Category, int Count)>(
                "SELECT category, COUNT(*) FROM audit_log GROUP BY category ORDER BY COUNT(*) DESC")).ToList();
            var total = byCategory.Sum(c => c.Count);
            var recentFailures = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_log WHERE success = 0 AND `timestamp` > DATE_SUB(NOW(), INTERVAL 7 DAY)");
            var today = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_log WHERE `timestamp` > DATE_SUB(NOW(), INTERVAL 1 DAY)");
            return McpResult.Success(new
            {
                total,
                today,
                recentFailures,
                byCategory = byCategory.Select(c => new { category = c.Category, count = c.Count })
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "activity_summary failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "activity_detail")]
    [Description("One audit entry detail-card: full state_before/state_after + notes.")]
    public async Task<string> Detail(
        [Description("Audit log row id.")] long id)
    {
        if (id <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "id must be positive").ToJson();
        try
        {
            using var conn = _db.Admin();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT id, `timestamp`, operator, operator_ip AS operatorIp,
                         category, action, target_type AS targetType,
                         target_name AS targetName, target_id AS targetId,
                         ra_command AS raCommand, ra_response AS raResponse,
                         state_before AS stateBefore, state_after AS stateAfter,
                         is_reversible AS isReversible, success, notes, batch_id AS batchId
                  FROM audit_log WHERE id = @id", new { id });
            return McpResult.Success(row is null ? new { found = false, id } : new { found = true, entry = row }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "activity_detail failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
