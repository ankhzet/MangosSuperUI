using System.ComponentModel;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Wiki corpus reads. Backed by <see cref="WikiDocStore"/> (filesystem) +
/// <see cref="WikiSearchStore"/> (FULLTEXT search) + <see cref="WikiIndexer"/>
/// (background reindexer). Read-only.
/// </summary>
[McpServerToolType]
public class WikiTools
{
    private readonly WikiDocStore _store;
    private readonly WikiSearchStore _search;
    private readonly WikiIndexer _indexer;
    private readonly ILogger<WikiTools> _log;

    public WikiTools(WikiDocStore store, WikiSearchStore search, WikiIndexer indexer, ILogger<WikiTools> log)
    {
        _store = store;
        _search = search;
        _indexer = indexer;
        _log = log;
    }

    [McpServerTool(Name = "wiki_search")]
    [Description(
        "Full-text search across the wiki corpus (code docs, lua capabilities, " +
        "rules). Returns hits with scores, coverage tier, and ready/notice signals. " +
        "Returns {ready:false, notice:'...'} if the index hasn't built yet.")]
    public async Task<string> Search(
        [Description("Search query.")] string query,
        [Description("Max hits (default 20, hard cap 50).")] int take = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return McpResult.Failure(ErrorCodes.InvalidInput, "query cannot be empty").ToJson();
        if (!_search.Configured)
            return McpResult.Success(new { ready = false, notice = "search index not configured" }).ToJson();
        try
        {
            var capped = Math.Clamp(take, 1, 50);
            var resp = await _search.SearchAsync(query, capped, ct);
            return McpResult.Success(resp).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "wiki_search failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "wiki_page")]
    [Description(
        "Render one wiki page (Markdown) to HTML with auto-links + table of contents. " +
        "Returns {found:false} when the path doesn't exist. Use wiki_tree to find paths.")]
    public string Page(
        [Description("Page path relative to the wiki root (no .md extension). E.g. 'core/world-state'.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return McpResult.Failure(ErrorCodes.InvalidInput, "path cannot be empty").ToJson();
        var page = _store.Page(path);
        return McpResult.Success(page is null
            ? new { found = false, path }
            : new { found = true, path, page }
        ).ToJson();
    }

    [McpServerTool(Name = "wiki_tree")]
    [Description(
        "Folder-mirrored nav tree for the wiki (dir/file/page with optional class-group " +
        "interleaving). Use the returned `path` values as inputs to wiki_page.")]
    public string Tree()
    {
        try
        {
            var tree = _store.Tree();
            return McpResult.Success(tree).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "wiki_tree failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "wiki_stats")]
    [Description(
        "Corpus summary: page count, total bytes, root path, root-exists flag.")]
    public string Stats() => McpResult.Success(new WikiStatsPayload(
        RootExists: _store.RootExists,
        Root: _store.Root,
        Stats: _store.Stats())).ToJson();

    [McpServerTool(Name = "wiki_index_status")]
    [Description(
        "Live status of the wiki indexer: Building / Done / Total / " +
        "LastCompletedUtc / LastError. Use wiki_reindex to force a rebuild.")]
    public string IndexStatus() => McpResult.Success(new { status = _indexer.Status }).ToJson();

    [McpServerTool(Name = "wiki_reindex")]
    [Description(
        "Force a full wiki reindex now. Returns true if a rebuild was started, " +
        "false if one is already running.")]
    public string Reindex()
    {
        try
        {
            var started = _indexer.ForceReindex();
            return McpResult.Success(new { started }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "wiki_reindex failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    /// <summary>
    /// Concrete payload type for <c>wiki_stats</c> so the JsonSerializer
    /// source-generator can resolve it (anonymous types break the
    /// generator's metadata). Same shape as the original anonymous object.
    /// </summary>
    public sealed record WikiStatsPayload(bool RootExists, string Root, WikiStats Stats);
}
