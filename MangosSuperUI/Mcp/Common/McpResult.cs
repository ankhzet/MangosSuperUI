using System.Text.Json;
using System.Text.Json.Serialization;

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

    public string ToJson() => JsonSerializer.Serialize(this, McpResultJsonContext.Default.McpResult);

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
/// AOT-friendly JSON serializer for <see cref="McpResult"/>. Falls back to
/// the reflective serializer if the source generator can't see the payload
/// type — payload types are arbitrary `object?` so this is the best we can
/// do at compile time. The middle ground: keep the envelope itself in a
/// source-generated context so the `ok`/`data`/`error` shape is locked.
/// </summary>
[JsonSerializable(typeof(McpResult))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(Dictionary<string, object>))]
public partial class McpResultJsonContext : JsonSerializerContext { }
