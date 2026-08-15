using System.ComponentModel;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// C++ source code index reads. Backed by <see cref="SourceIndexerService"/>
/// which scans + parses the vmangos source tree at startup. Read-only —
/// source_reindex is the only mutating call.
/// </summary>
[McpServerToolType]
public class SourceTools
{
    private readonly SourceIndexerService _indexer;
    private readonly ILogger<SourceTools> _log;

    public SourceTools(SourceIndexerService indexer, ILogger<SourceTools> log)
    {
        _indexer = indexer;
        _log = log;
    }

    [McpServerTool(Name = "source_index_progress")]
    [Description(
        "Live progress of the source indexer: phase (idle/scanning/parsing/done), " +
        "done count, total count, last-completed timestamp.")]
    public string IndexProgress() => McpResult.Success(new
    {
        _indexer.CurrentProgress,
        isIndexing = _indexer.IsIndexing,
        hasIndex = _indexer.GetIndex() is not null
    }).ToJson();

    [McpServerTool(Name = "source_stats")]
    [Description(
        "Source-map rollup: total functions/types/enums/files/lines/edges, plus " +
        "top hubs (most-connected symbols), top callers, top complex, top deep.")]
    public string Stats()
    {
        var idx = _indexer.GetIndex();
        if (idx is null)
            return McpResult.Success(new { ready = false, notice = "index not built yet" }).ToJson();

        var hubSymbols = idx.Symbols.Values
            .OrderByDescending(s => s.CallsOut.Count + s.CalledBy.Count)
            .Take(15)
            .Select(s => new { id = s.Id, totalConnections = s.CallsOut.Count + s.CalledBy.Count, callsOut = s.CallsOut.Count, calledBy = s.CalledBy.Count })
            .ToList();
        var mostCalled = idx.Symbols.Values
            .OrderByDescending(s => s.CalledBy.Count)
            .Take(15)
            .Select(s => new { id = s.Id, callerCount = s.CalledBy.Count })
            .ToList();
        var mostComplex = idx.Symbols.Values
            .OrderByDescending(s => s.CallsOut.Count)
            .Take(15)
            .Select(s => new { id = s.Id, callCount = s.CallsOut.Count, complexity = s.Complexity })
            .ToList();

        return McpResult.Success(new
        {
            meta = new
            {
                totalFunctions = idx.TotalSymbols,
                totalTypes = idx.TotalTypes,
                totalEnums = idx.TotalEnums,
                totalFiles = idx.TotalFiles,
                totalLines = idx.TotalLines,
                totalEdges = idx.Symbols.Values.Sum(s => s.CallsOut.Count),
                indexedAt = idx.IndexedAt,
                sourcePath = idx.SourcePath
            },
            hubs = new
            {
                hubFunctions = hubSymbols,
                topCallers = mostCalled,
                topComplex = mostComplex
            }
        }).ToJson();
    }

    [McpServerTool(Name = "source_search")]
    [Description(
        "Keyword search across symbols/types/enums/files. `kind` filters to " +
        "one of: all|symbol|type|enum|file. Returns up to `max` matches.")]
    public string Search(
        [Description("Search query.")] string query,
        [Description("Kind filter: all|symbol|type|enum|file.")] string kind = "all",
        [Description("Max matches (hard cap 200).")] int max = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
            return McpResult.Failure(ErrorCodes.InvalidInput, "query cannot be empty").ToJson();
        var capped = Math.Clamp(max, 1, 200);
        try
        {
            var hits = _indexer.Search(query, kind, capped);
            return McpResult.Success(new { query, kind, count = hits.Count, results = hits }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "source_search failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "source_smart_search")]
    [Description(
        "Resolve a C++ expression like `me->IsDead()` or `pPlayer->GetSession()` to " +
        "candidate symbols with inheritance-aware ranking. Returns the resolved " +
        "expression type + match list.")]
    public string SmartSearch(
        [Description("C++ expression.")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return McpResult.Failure(ErrorCodes.InvalidInput, "query cannot be empty").ToJson();
        try
        {
            var result = _indexer.SmartSearch(query);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "source_smart_search failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "source_get_symbol")]
    [Description(
        "One symbol's details: qualified id, calls-out, called-by, complexity, " +
        "line number, file. Returns null when not indexed.")]
    public string GetSymbol(
        [Description("Qualified symbol name (use source_search or smart_search to find).")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return McpResult.Failure(ErrorCodes.InvalidInput, "id cannot be empty").ToJson();
        var sym = _indexer.GetSymbol(id);
        return McpResult.Success(sym is null ? new { found = false, id } : new { found = true, symbol = sym }).ToJson();
    }

    [McpServerTool(Name = "source_get_type")]
    [Description("One type's details (class/struct), including qualified methods and base types.")]
    public string GetType(
        [Description("Type name.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return McpResult.Failure(ErrorCodes.InvalidInput, "name cannot be empty").ToJson();
        var t = _indexer.GetType(name);
        return McpResult.Success(t is null ? new { found = false, name } : new { found = true, type = t }).ToJson();
    }

    [McpServerTool(Name = "source_get_enum")]
    [Description("One enum's details with member values.")]
    public string GetEnum(
        [Description("Enum name.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return McpResult.Failure(ErrorCodes.InvalidInput, "name cannot be empty").ToJson();
        var e = _indexer.GetEnum(name);
        return McpResult.Success(e is null ? new { found = false, name } : new { found = true, @enum = e }).ToJson();
    }

    [McpServerTool(Name = "source_get_file")]
    [Description("One source file's metadata: defined symbols, line count, extension.")]
    public string GetFile(
        [Description("File path relative to the source root.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return McpResult.Failure(ErrorCodes.InvalidInput, "path cannot be empty").ToJson();
        var f = _indexer.GetFile(path);
        return McpResult.Success(f is null ? new { found = false, path } : new { found = true, file = f }).ToJson();
    }

    [McpServerTool(Name = "source_inheritance_chain")]
    [Description("Returns the inheritance chain for a type (base classes up to root).")]
    public string InheritanceChain(
        [Description("Type name.")] string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return McpResult.Failure(ErrorCodes.InvalidInput, "type cannot be empty").ToJson();
        var chain = _indexer.GetType(type)?.Inherits ?? new List<string>();
        return McpResult.Success(new { type, chain }).ToJson();
    }

    [McpServerTool(Name = "source_export_trace")]
    [Description(
        "Caller/called tree around a symbol up to `depth` levels. Includes types " +
        "and headers by default. Heavy call for deeply-nested symbols — start " +
        "with depth=2.")]
    public string ExportTrace(
        [Description("Root symbol qualified name.")] string root,
        [Description("Depth (default 2, hard cap 5).")] int depth = 2,
        [Description("Include types in the trace.")] bool includeTypes = true,
        [Description("Include header-only symbols.")] bool includeHeaders = true)
    {
        if (string.IsNullOrWhiteSpace(root))
            return McpResult.Failure(ErrorCodes.InvalidInput, "root cannot be empty").ToJson();
        var capped = Math.Clamp(depth, 1, 5);
        var trace = _indexer.ExportTrace(root, capped, includeTypes, includeHeaders);
        return McpResult.Success(trace is null ? new { found = false, root } : trace).ToJson();
    }

    [McpServerTool(Name = "source_topic_explore")]
    [Description(
        "Group symbols by topic area for a free-text query. Returns a TopicReport " +
        "with per-topic symbol lists.")]
    public string TopicExplore(
        [Description("Topic query.")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return McpResult.Failure(ErrorCodes.InvalidInput, "query cannot be empty").ToJson();
        var report = _indexer.ExploreTopic(query);
        return McpResult.Success(report is null ? new { found = false, query } : report).ToJson();
    }

    [McpServerTool(Name = "source_find_string_references")]
    [Description(
        "Find indexed symbols whose bodies contain the given literal string(s). " +
        "Useful for tracing how a string column or table name is read in code.")]
    public string FindStringReferences(
        [Description("One literal string to search for.")] string needle,
        [Description("If true, require ALL needles to be present (only meaningful when multiple).")] bool requireAll = false,
        [Description("Max matches (0 = unlimited, hard cap 500).")] int max = 0)
    {
        if (string.IsNullOrEmpty(needle))
            return McpResult.Failure(ErrorCodes.InvalidInput, "needle cannot be empty").ToJson();
        var cap = Math.Clamp(max, 0, 500);
        try
        {
            var result = _indexer.FindStringReferences(new[] { needle }, requireAll, cap);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "source_find_string_references failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "source_find_member_references")]
    [Description(
        "Find consumers of a struct field. Useful for 'where is this DB column " +
        "read/written in code?' questions.")]
    public string FindMemberReferences(
        [Description("Field/member name.")] string needle,
        [Description("Max matches (0 = unlimited, hard cap 500).")] int max = 0)
    {
        if (string.IsNullOrEmpty(needle))
            return McpResult.Failure(ErrorCodes.InvalidInput, "needle cannot be empty").ToJson();
        var cap = Math.Clamp(max, 0, 500);
        try
        {
            var result = _indexer.FindMemberReferences(new[] { needle }, false, cap);
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "source_find_member_references failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "source_reindex")]
    [Description(
        "Force a full rebuild of the source index from the configured source " +
        "path. Long-running (minutes); call source_index_progress to poll.")]
    public async Task<string> Reindex(
        [Description("Source path override (defaults to VmangosSourcePath).")] string? sourcePath = null)
    {
        try
        {
            var result = await _indexer.ReindexAsync(sourcePath ?? "");
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "source_reindex failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }
}
