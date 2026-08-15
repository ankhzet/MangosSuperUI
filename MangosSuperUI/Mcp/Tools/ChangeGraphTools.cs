using System.ComponentModel;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// audit_log-as-graph: overview, batches, entries, entry detail, and undo.
/// `changegraph_revert_*` tools require the <c>baseline</c> capability — they
/// reverse irreversible DB writes that were audit-logged at write time
/// (with captured before-state).
/// </summary>
[McpServerToolType]
public class ChangeGraphTools
{
    private readonly ChangeGraphService _graph;
    private readonly McpCallContext _ctx;
    private readonly ILogger<ChangeGraphTools> _log;

    public ChangeGraphTools(ChangeGraphService graph, McpCallContext ctx, ILogger<ChangeGraphTools> log)
    {
        _graph = graph;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "changegraph_overview")]
    [Description(
        "Domain rollup: changes / batches / revertable / reverted / failures " +
        "per domain (loot, spells, items, world, professions, bots, database, " +
        "config, system). Optional filters: search, operator, days, show.")]
    public async Task<string> Overview(
        [Description("Optional name substring filter.")] string? search = null,
        [Description("Optional operator label (e.g. 'claude-desktop').")] string? operatorLabel = null,
        [Description("Days of history (null = all).")] int? days = null,
        [Description("Show filter: 'all', 'revertable', 'reverted', 'failures'.")] string? show = null)
    {
        try
        {
            var filter = MakeFilter(search, operatorLabel, days, show);
            var result = await _graph.GetOverviewAsync(filter);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "changegraph_overview failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "changegraph_batches")]
    [Description("Batch list within one domain (paginated).")]
    public async Task<string> Batches(
        [Description("Domain key (e.g. 'loot', 'spells', 'items').")] string domain,
        [Description("Optional search.")] string? search = null,
        [Description("Optional operator filter.")] string? operatorLabel = null,
        [Description("Days of history.")] int? days = null,
        [Description("Show filter.")] string? show = null,
        [Description("Page number, 1-based.")] int page = 1)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return McpResult.Failure(ErrorCodes.InvalidInput, "domain required").ToJson();
        try
        {
            var filter = MakeFilter(search, operatorLabel, days, show);
            var result = await _graph.GetBatchesAsync(domain, filter, page, 40);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "changegraph_batches failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "changegraph_entries")]
    [Description("Audit entries within one batch (paginated).")]
    public async Task<string> Entries(
        [Description("Batch key (from changegraph_batches).")] string batch,
        [Description("Page number, 1-based.")] int page = 1)
    {
        if (string.IsNullOrWhiteSpace(batch))
            return McpResult.Failure(ErrorCodes.InvalidInput, "batch required").ToJson();
        try
        {
            var result = await _graph.GetEntriesAsync(batch, page, 100);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "changegraph_entries failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "changegraph_entry")]
    [Description("One audit entry with field diff vs baseline + revert description.")]
    public async Task<string> Entry(
        [Description("Audit log row id.")] long id)
    {
        if (id <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "id must be positive").ToJson();
        try
        {
            var result = await _graph.GetEntryAsync(id);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "changegraph_entry failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "changegraph_revert_entry")]
    [McpCapability(McpCapability.Baseline)]
    [Description(
        "Undo one logged change. Re-uses the captured before-state from the " +
        "audit row. Returns a RevertResult with success/error/rowsAffected/summary.")]
    public async Task<string> RevertEntry(
        [Description("Audit log row id to revert.")] long id)
    {
        if (id <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "id must be positive").ToJson();
        try
        {
            var result = await _graph.RevertEntryAsync(id, _ctx.RemoteIp);
            return McpResult.Success(new
            {
                success = result.Success,
                error = result.Error,
                rowsAffected = result.RowsAffected,
                summary = result.Summary
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "changegraph_revert_entry failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "changegraph_revert_batch")]
    [McpCapability(McpCapability.Baseline)]
    [Description(
        "Undo a whole batch (newest-first). Skips registry-owned rows. " +
        "Returns aggregate result.")]
    public async Task<string> RevertBatch(
        [Description("Batch key.")] string batch)
    {
        if (string.IsNullOrWhiteSpace(batch))
            return McpResult.Failure(ErrorCodes.InvalidInput, "batch required").ToJson();
        try
        {
            var result = await _graph.RevertBatchAsync(batch, _ctx.RemoteIp);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "changegraph_revert_batch failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    private static ChangeGraphService.GraphFilter MakeFilter(string? search, string? op, int? days, string? show)
        => new() { Search = search, Operator = op, Days = days, Show = show };
}
