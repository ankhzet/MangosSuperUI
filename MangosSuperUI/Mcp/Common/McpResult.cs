using System.Text.Json;
using System.Text.Json.Serialization;
using MangosSuperUI.Services;

namespace MangosSuperUI.Mcp.Common;

/// <summary>
/// Standard JSON envelope returned by every MCP tool. The MCP SDK itself
/// wraps tool outputs in a `content` array; the <see cref="McpResult"/>
/// payload is what's INSIDE that wrapper, so the LLM gets one of two
/// predictable shapes and can `try/catch` cleanly:
///
///   { "ok": true,  "data": { ...payload... } }
///   { "ok": false, "error": { "code": "RA_DISCONNECTED", "message": "...",
///                              "retryable": true, "hint": "ra_reconnect" } }
///
/// Tools SHOULD return <see cref="McpResult.Ok(object?)"/> or
/// <see cref="McpResult.Fail(string, string, bool?, string?)"/> rather than
/// free-form JSON. The middleware / audit pipeline relies on this shape to
/// emit structured logs.
/// </summary>
public sealed class McpResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }

    [JsonPropertyName("error")]
    public McpError? Error { get; init; }

    public string ToJson()
    {
        // Happy path: source-gen serializer. Fast and zero-alloc.
        try
        {
            return JsonSerializer.Serialize(this, McpResultJsonContext.Default.McpResult);
        }
        catch (NotSupportedException ex) when (ex.Message.Contains("JsonTypeInfo metadata", StringComparison.Ordinal))
        {
            // Fallback for payload types the source generator couldn't see
            // (anonymous types, types declared in inline lambdas, etc.).
            // Same wire format, just less performant.
            return JsonSerializer.Serialize(this, McpResultJsonFallback.Options);
        }
    }

    public static McpResult Success(object? data) => new() { Ok = true, Data = data };

    public static McpResult Failure(string code, string message, bool? retryable = null, string? hint = null) =>
        new()
        {
            Ok = false,
            Error = new McpError { Code = code, Message = message, Retryable = retryable, Hint = hint }
        };

    public static McpResult FromException(Exception ex, string? code = null) =>
        Failure(code ?? "INTERNAL", ex.Message, retryable: false);
}

public sealed class McpError
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "INTERNAL";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; init; }

    [JsonPropertyName("hint")]
    public string? Hint { get; init; }
}

/// <summary>
/// Canonical error codes. Adding a new one? Update <see cref="ErrorCodes.All"/>
/// and the docs in parallel — agents grep on these strings.
/// </summary>
public static class ErrorCodes
{
    public const string RaDisconnected   = "RA_DISCONNECTED";
    public const string DbUnavailable    = "DB_UNAVAILABLE";
    public const string DbTimeout        = "DB_TIMEOUT";
    public const string InvalidInput     = "INVALID_INPUT";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string NotFound         = "NOT_FOUND";
    public const string Partial          = "PARTIAL";
    public const string Conflict         = "CONFLICT";
    public const string Internal         = "INTERNAL";
    public const string RateLimited      = "RATE_LIMITED";

    public static readonly string[] All =
    {
        RaDisconnected, DbUnavailable, DbTimeout, InvalidInput,
        PermissionDenied, NotFound, Partial, Conflict, Internal, RateLimited
    };
}

/// <summary>
/// AOT-friendly JSON serializer for <see cref="McpResult"/>. The
/// payload types we explicitly annotate here round-trip through the
/// source-gen serializer. Anything else (anonymous types, runtime
/// records, etc.) falls back to the reflective serializer via the
/// resilient wrapper in <see cref="McpResult.ToJson"/>.
///
/// We don't need pure AOT here — Dapper uses heavy reflection throughout
/// the codebase, so any AOT benefit would be theoretical. The source-gen
/// context exists mostly to lock the envelope shape and speed up the
/// happy path for the Wiki types that are returned via object.
/// </summary>
[JsonSerializable(typeof(McpResult))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(WikiSearchResponse))]
[JsonSerializable(typeof(WikiSearchHit))]
[JsonSerializable(typeof(WikiStats))]
[JsonSerializable(typeof(WikiTree))]
[JsonSerializable(typeof(WikiPage))]
[JsonSerializable(typeof(WikiNode))]
[JsonSerializable(typeof(Tools.WikiTools.WikiStatsPayload))]
public partial class McpResultJsonContext : JsonSerializerContext { }

/// <summary>
/// Reflection-only options used as a fallback when the source-gen context
/// can't resolve a payload type (e.g. anonymous types from inline
/// `new { ... }` returns). Identical settings to the source-gen context
/// so wire format stays consistent.
/// </summary>
file static class McpResultJsonFallback
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
