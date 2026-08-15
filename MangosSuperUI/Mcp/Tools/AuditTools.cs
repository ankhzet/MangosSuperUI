using System.ComponentModel;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Read-only access to the audit_log table. Lets an LLM review its own (or
/// previous operators') actions, find what command ran against a given target,
/// or surface recent failures.
/// </summary>
[McpServerToolType]
public class AuditTools
{
    private readonly AuditService _audit;
    private readonly ILogger<AuditTools> _logger;

    public AuditTools(AuditService audit, ILogger<AuditTools> logger)
    {
        _audit = audit;
        _logger = logger;
    }

    [McpServerTool(Name = "audit_recent")]
    [Description(
        "Return the N most recent audit_log rows. Optionally filter by category " +
        "(e.g. 'RA', 'BotBridge', 'World', 'Config'). Use this to see what " +
        "happened recently — both successful commands and failures show up here.")]
    public async Task<string> Recent(
        [Description("Number of rows to return (default 20, hard cap 200).")] int count = 20,
        [Description("Optional category filter, e.g. 'RA', 'BotBridge', 'World'.")] string? category = null)
    {
        var capped = Math.Clamp(count, 1, 200);
        try
        {
            var rows = await _audit.GetRecentAsync(capped, category);
            return McpResult.Success(new
            {
                count = rows.Count(),
                rows = rows.Select(r => new
                {
                    r.Id,
                    r.Timestamp,
                    r.Operator,
                    r.OperatorIp,
                    r.Category,
                    r.Action,
                    r.TargetType,
                    r.TargetName,
                    r.RaCommand,
                    r.Success,
                    r.Notes
                })
            }).ToJson();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "audit_recent failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "audit_target_history")]
    [Description(
        "Return recent audit_log entries that targeted a specific entity. " +
        "Target types observed in MSUI include 'player', 'account', 'guild', 'item', 'quest'. " +
        "Useful for 'who banned this account?' or 'what did I do to that player?'.")]
    public async Task<string> TargetHistory(
        [Description("Target type, e.g. 'player', 'account', 'guild'.")] string targetType,
        [Description("Target name (character name, account username, etc.).")] string targetName,
        [Description("Number of rows to return (default 20, hard cap 100).")] int count = 20)
    {
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetName))
            return McpResult.Failure(ErrorCodes.InvalidInput, "targetType and targetName required").ToJson();

        var capped = Math.Clamp(count, 1, 100);
        try
        {
            var rows = await _audit.GetTargetHistoryAsync(targetType, targetName, capped);
            return McpResult.Success(new
            {
                count = rows.Count(),
                rows = rows.Select(r => new
                {
                    r.Id,
                    r.Timestamp,
                    r.Operator,
                    r.Category,
                    r.Action,
                    r.RaCommand,
                    r.Success,
                    r.Notes
                })
            }).ToJson();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "audit_target_history failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
