namespace MangosSuperUI.Mcp.Auth;

/// <summary>
/// Per-tool-call audit context. Resolved from the current HttpContext by the
/// middleware (caller label) and the JSON-RPC method inspector (tool name),
/// then injected as a scoped DI service so tool methods can stamp audit_log
/// rows with who invoked them instead of the legacy `"mcp"` placeholder.
///
/// Lifetime is scoped — one instance per HTTP request. Outside an MCP call
/// (e.g. background services) every property falls back to the
/// "anonymous"/empty defaults so audit rows still get written.
/// </summary>
public sealed class McpCallContext
{
    public const string HttpContextKey = "McpCallContext";

    /// <summary>
    /// The token label (e.g. "claude-desktop", "ci-readonly", "legacy-MCP_AUTH_TOKEN").
    /// Empty when the call is unauthenticated / out-of-band.
    /// </summary>
    public string CallerLabel { get; init; } = "anonymous";

    /// <summary>
    /// The MCP tool name being invoked, e.g. "ra_kick_player".
    /// Empty when the call isn't a tool invocation (e.g. notifications).
    /// </summary>
    public string ToolName { get; init; } = "";

    /// <summary>
    /// Remote IP from the request, or empty for background work.
    /// </summary>
    public string RemoteIp { get; init; } = "";

    /// <summary>
    /// Best-effort JSON-RPC request id, useful for tying an audit row to the
    /// client's log of the call.
    /// </summary>
    public string RequestId { get; init; } = "";

    /// <summary>
    /// Convenience: "label/tool" or "label/tool@ip" — what gets stamped in
    /// the `operator` column of audit_log for MCP-initiated actions.
    /// </summary>
    public string Operator =>
        string.IsNullOrEmpty(ToolName)
            ? CallerLabel
            : string.IsNullOrEmpty(RemoteIp)
                ? $"{CallerLabel}/{ToolName}"
                : $"{CallerLabel}/{ToolName}@{RemoteIp}";

    public static McpCallContext Empty { get; } = new();
}
