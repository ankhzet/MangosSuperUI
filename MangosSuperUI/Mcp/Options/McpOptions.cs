namespace MangosSuperUI.Mcp.Options;

public class McpOptions
{
    public const string SectionName = "Mcp";

    public bool Enabled { get; set; } = true;

    public string Route { get; set; } = "/mcp";

    public McpAuthOptions Auth { get; set; } = new();

    public McpAuditOptions Audit { get; set; } = new();
}

public class McpAuthOptions
{
    /// <summary>
    /// If true, every request must carry a valid bearer token in the
    /// `Authorization: Bearer &lt;token&gt;` header. Disable only when the
    /// endpoint is bound to a trusted loopback and you don't mind every
    /// local process being able to call tools.
    /// </summary>
    public bool RequireBearerToken { get; set; } = true;

    /// <summary>
    /// Env var name holding the legacy single-token fallback. Kept for
    /// back-compat with Phase-0 deployments: when `Tokens` is empty AND this
    /// env var is set, the middleware accepts that token with the full
    /// capability set (superuser). New deployments should use `Tokens`.
    /// </summary>
    public string EnvVarName { get; set; } = "MCP_AUTH_TOKEN";

    /// <summary>
    /// Capability-tagged token allowlist. Each entry binds a token to the
    /// label recorded in audit_log and to the set of capability tags the
    /// token holder may invoke. See `McpCapability` for the canonical list.
    /// </summary>
    public List<McpTokenEntry> Tokens { get; set; } = new();

    /// <summary>
    /// CORS origins allowed to call the MCP endpoint from a browser. Empty
    /// means CORS is off (recommended for local use). Add origins only when
    /// you intentionally want browser-based clients.
    /// </summary>
    public string[] AllowOrigins { get; set; } = Array.Empty<string>();
}

public class McpTokenEntry
{
    public string Token { get; set; } = "";
    public string Label { get; set; } = "";
    public string[] Capabilities { get; set; } = Array.Empty<string>();
}

public class McpAuditOptions
{
    public string OperatorName { get; set; } = "mcp-client";
}

/// <summary>
/// Canonical capability tags. The middleware rejects any tool call whose
/// declared `McpCapability` set is not a subset of the calling token's
/// capabilities. Use the lowercase constants below — never spell the tag
/// inline in tool code.
/// </summary>
public static class McpCapability
{
    public const string Read      = "read";
    public const string Ra        = "ra";
    public const string Process   = "process";
    public const string WriteDb   = "write_db";
    public const string Worlds    = "worlds";
    public const string Bots      = "bots";
    public const string Patches   = "patches";
    public const string Baseline  = "baseline";
    public const string Lootifier = "lootifier";
    public const string Retexture = "retexture";

    /// <summary>
    /// The implicit superuser set used by the legacy single-token fallback.
    /// Every capability in one place so adding a new tag doesn't silently
    /// exclude the legacy token.
    /// </summary>
    public static readonly string[] All = new[]
    {
        Read, Ra, Process, WriteDb, Worlds, Bots, Patches, Baseline, Lootifier, Retexture
    };
}
