using System.Reflection;
using MangosSuperUI.Mcp.Options;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Auth;

/// <summary>
/// Tool name → set of required capability tags. Built once at startup by
/// reflecting over every public method in the supplied assemblies that
/// carries both `[McpServerTool]` and (optionally) `[McpCapability]`.
/// Methods with no `[McpCapability]` default to `[McpCapability.Read]`.
///
/// Stored as an array of frozen `Lookup` snapshots so the middleware does a
/// linear scan over ~120 entries with zero allocations on the hot path.
/// </summary>
public sealed class McpToolCapabilityRegistry
{
    private readonly Lookup[] _entries;
    private readonly ILogger<McpToolCapabilityRegistry> _logger;

    public McpToolCapabilityRegistry(Lookup[] entries, ILogger<McpToolCapabilityRegistry> logger)
    {
        _entries = entries;
        _logger = logger;
    }

    /// <summary>
    /// Returns the capability set required for `toolName`, or null if the
    /// tool is not registered (i.e. the SDK knows about a tool we haven't
    /// declared capabilities for — treated as a configuration error).
    /// </summary>
    public string[]? GetRequiredCapabilities(string toolName)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (string.Equals(_entries[i].ToolName, toolName, StringComparison.Ordinal))
                return _entries[i].Capabilities;
        }
        return null;
    }

    public int Count => _entries.Length;

    /// <summary>
    /// Reflect over `Mcp/Tools/`-style tool types and collect the
    /// tool-name → capabilities map. Called once at startup.
    /// </summary>
    public static McpToolCapabilityRegistry FromAssemblyScans(
        IEnumerable<Assembly> assemblies,
        ILogger<McpToolCapabilityRegistry> logger)
    {
        var entries = new List<Lookup>();

        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type is null) continue;
                // Convention: tools live in MangosSuperUI.Mcp.Tools namespace.
                // Cheap guard so we don't scan every controller / service.
                if (type.Namespace is null ||
                    !type.Namespace.StartsWith("MangosSuperUI.Mcp.Tools", StringComparison.Ordinal))
                    continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (toolAttr is null) continue;

                    var caps = method.GetCustomAttributes<McpCapabilityAttribute>()
                        .Select(a => a.Capability)
                        .Distinct()
                        .ToArray();
                    if (caps.Length == 0) caps = new[] { McpCapability.Read };

                    entries.Add(new Lookup(toolAttr.Name, caps));
                }
            }
        }

        var frozen = entries.OrderBy(e => e.ToolName, StringComparer.Ordinal).ToArray();
        logger.LogInformation(
            "MCP capability registry built: {Count} tools indexed across {Asm} assemblies",
            frozen.Length,
            assemblies.Count());

        return new McpToolCapabilityRegistry(frozen, logger);
    }

    public readonly record struct Lookup(string ToolName, string[] Capabilities);
}
