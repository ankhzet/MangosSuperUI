namespace MangosSuperUI.Mcp.Auth;

/// <summary>
/// Declares the capability tags required to invoke a tool method. Multiple
/// attributes may be stacked — the caller must satisfy ALL declared tags.
/// Methods with no attribute default to requiring `McpCapability.Read`.
///
/// Usage:
///   [McpServerTool(Name = "ra_kick_player")]
///   [McpCapability(McpCapability.Ra)]
///   public async Task&lt;string&gt; KickPlayer(string name) { ... }
///
/// The middleware enforces these at call time, not at registration time,
/// so a token granted only `read` cannot trigger an `ra_*` tool even if the
/// tool is registered.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class McpCapabilityAttribute : Attribute
{
    public string Capability { get; }

    public McpCapabilityAttribute(string capability)
    {
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
    }
}
