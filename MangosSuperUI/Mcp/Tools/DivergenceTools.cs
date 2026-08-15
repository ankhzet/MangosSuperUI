using System.ComponentModel;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Live drift vs og_* baselines (the *state* view). Reads default to <c>read</c>.
/// </summary>
[McpServerToolType]
public class DivergenceTools
{
    private readonly DivergenceService _div;
    private readonly ILogger<DivergenceTools> _log;

    public DivergenceTools(DivergenceService div, ILogger<DivergenceTools> log)
    {
        _div = div;
        _log = log;
    }

    [McpServerTool(Name = "divergence_overview")]
    [Description(
        "Per-domain totals: modified / added / removed / lootified / newlyAdded, " +
        "plus scannedAt timestamp and any errors. " +
        "`mode='tracked'` includes only audit-named + custom ranges; " +
        "`mode='deep'` includes every difference.")]
    public async Task<string> Overview(
        [Description("'tracked' or 'deep'.")] string mode = "tracked")
    {
        try
        {
            var result = await _div.GetOverviewAsync(mode);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "divergence_overview failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "divergence_tree")]
    [Description(
        "Variable-depth drill into one domain: bucket → loot-kind → " +
        "profession/instance → boss → baseitem → leaf name. " +
        "`path` is a slash-separated path (empty = top level).")]
    public async Task<string> Tree(
        [Description("Domain key ('items', 'spells', 'world', 'loot').")] string domain,
        [Description("'tracked' or 'deep'.")] string mode = "tracked",
        [Description("Slash-separated drill path (empty = top).")] string? path = null,
        [Description("Optional name substring filter.")] string? search = null,
        [Description("Max rows (default 200, hard cap 1000).")] int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return McpResult.Failure(ErrorCodes.InvalidInput, "domain required").ToJson();
        var capped = Math.Clamp(limit, 1, 1000);
        try
        {
            var result = await _div.GetTreeAsync(domain, mode, path, search, capped);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "divergence_tree failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "divergence_invalidate_cache")]
    [Description(
        "Drop the per-surface scan cache so the next divergence call re-scans. " +
        "Use after a batch of content edits if you don't want to wait for the " +
        "10-minute TTL.")]
    public string InvalidateCache()
    {
        try
        {
            _div.InvalidateCache();
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "divergence_invalidate_cache failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }
}
