using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MangosSuperUI.Mcp.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Mcp.Auth;

/// <summary>
/// Bearer-token middleware for the MCP endpoint.
///
/// Two layers of enforcement:
///
/// 1. **Authentication** — constant-time compare against the configured
///    token allowlist (capability-tagged) plus the legacy single-token
///    fallback (`McpAuthOptions.EnvVarName`) for back-compat with Phase-0
///    deployments.
///
/// 2. **Authorisation** — for tool-call JSON-RPC requests (POST /mcp with a
///    `tools/call` method), the middleware inspects the body to extract the
///    tool name and checks the caller's capability set against
///    <see cref="McpToolCapabilityRegistry"/>. Insufficient scope → 403.
///
/// `tools/list` and other meta methods are allowed with any authenticated
/// caller so clients can discover the surface before authenticating further
/// (the tool catalogue itself does not leak capability-gated names — we
/// filter it server-side so a read-only token doesn't see tools it can't
/// call).
/// </summary>
public class McpAuthMiddleware
{
    /// <summary>
    /// One-shot token generated at startup by <see cref="McpTokenBootstrap"/>
    /// when bearer auth is required but no token is configured. Treated as
    /// a superuser (granted every capability). Reset on every container
    /// restart — operators who want a stable token should set
    /// <c>MCP_AUTH_TOKEN</c> explicitly.
    /// </summary>
    public static string? GeneratedToken { get; set; }

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<McpOptions> _options;
    private readonly McpToolCapabilityRegistry _registry;
    private readonly ILogger<McpAuthMiddleware> _logger;

    public McpAuthMiddleware(
        RequestDelegate next,
        IOptionsMonitor<McpOptions> options,
        McpToolCapabilityRegistry registry,
        ILogger<McpAuthMiddleware> logger)
    {
        _next = next;
        _options = options;
        _registry = registry;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = _options.CurrentValue;
        var path = context.Request.Path.Value ?? "";

        if (!opts.Enabled || !ShouldProtect(path, opts.Route))
        {
            await _next(context);
            return;
        }

        if (!opts.Auth.RequireBearerToken)
        {
            await _next(context);
            return;
        }

        // ---- Authenticate ----
        var supplied = ExtractBearer(context.Request);
        var caller = ResolveCaller(opts.Auth, supplied);
        if (caller is null)
        {
            _logger.LogWarning(
                "MCP auth failure from {RemoteIp} for {Path}",
                context.Connection.RemoteIpAddress, path);
            await Reject(context, StatusCodes.Status401Unauthorized,
                "invalid_token",
                "Invalid or missing bearer token. " +
                "If no token is configured, one was auto-generated at startup — check the application logs.");
            return;
        }

        context.Items["McpCallerLabel"] = caller.Label;
        context.Items["McpCallerCapabilities"] = caller.Capabilities;

        // GET /mcp = server-sent events stream for stateful sessions.
        // We're stateless (Stateless = true), so GET has nothing to return
        // for us — reject so we don't leak capability info on a side channel.
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await Reject(context, StatusCodes.Status405MethodNotAllowed,
                "method_not_allowed", "Stateless transport: use POST.");
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await Reject(context, StatusCodes.Status405MethodNotAllowed,
                "method_not_allowed", "Only POST is supported.");
            return;
        }

        // ---- Authorize (per-tool) ----
        // tools/list and initialize are allowed for any authenticated caller.
        // tools/call must satisfy the capability requirements for the named tool.
        var (toolName, requestId, bodyBuffer) = await PeekToolCallAsync(context);
        context.Items[McpCallContext.HttpContextKey] = new McpCallContext
        {
            CallerLabel = caller.Label,
            ToolName = toolName ?? "",
            RemoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "",
            RequestId = requestId ?? "",
        };
        if (toolName is null)
        {
            // Non-tool-call request (initialize, notifications, ping, …) — pass through.
            context.Request.Body = bodyBuffer;
            await _next(context);
            return;
        }

        if (string.Equals(toolName, "tools/list", StringComparison.Ordinal))
        {
            context.Request.Body = bodyBuffer;
            await _next(context);
            return;
        }

        var required = _registry.GetRequiredCapabilities(toolName);
        if (required is null)
        {
            // Unknown tool name — let the SDK produce its own protocol error.
            context.Request.Body = bodyBuffer;
            await _next(context);
            return;
        }

        foreach (var cap in required)
        {
            if (!caller.Capabilities.Contains(cap, StringComparer.Ordinal))
            {
                _logger.LogWarning(
                    "MCP scope failure from {RemoteIp} label={Label}: tool={Tool} needs {Need}",
                    context.Connection.RemoteIpAddress, caller.Label, toolName, cap);
                await Reject(context, StatusCodes.Status403Forbidden,
                    "insufficient_scope",
                    $"Token '{caller.Label}' lacks capability '{cap}' required by '{toolName}'.");
                return;
            }
        }

        context.Request.Body = bodyBuffer;
        await _next(context);
    }

    // ---------- helpers ----------

    private static bool ShouldProtect(string path, string route)
    {
        if (string.IsNullOrEmpty(route)) return false;
        var routeNorm = route.StartsWith('/') ? route : "/" + route;
        return path.Equals(routeNorm, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(routeNorm + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractBearer(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("Authorization", out var header)) return null;
        var raw = header.ToString();
        const string prefix = "Bearer ";
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return raw.Substring(prefix.Length).Trim();
        return null;
    }

    private sealed class Caller
    {
        public string Label { get; }
        public IReadOnlyCollection<string> Capabilities { get; }
        public Caller(string label, IReadOnlyCollection<string> caps)
        {
            Label = label;
            Capabilities = caps;
        }
    }

    private static Caller? ResolveCaller(McpAuthOptions auth, string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return null;

        foreach (var entry in auth.Tokens)
        {
            if (string.IsNullOrEmpty(entry.Token)) continue;
            if (FixedTimeEquals(entry.Token, supplied))
            {
                var caps = entry.Capabilities is { Length: > 0 } ? entry.Capabilities : McpCapability.All;
                return new Caller(string.IsNullOrWhiteSpace(entry.Label) ? "token" : entry.Label, caps);
            }
        }

        // Legacy single-token fallback: env-var token grants everything.
        var legacy = Environment.GetEnvironmentVariable(auth.EnvVarName);
        if (!string.IsNullOrEmpty(legacy) && FixedTimeEquals(legacy, supplied))
        {
            return new Caller("legacy-" + auth.EnvVarName, McpCapability.All);
        }

        // Auto-generated fallback: a 256-bit superuser token created at
        // startup by McpTokenBootstrap when auth is required but no other
        // token is configured. Lets a fresh solo deployment be usable
        // without forcing the operator to mint a token by hand.
        var generated = GeneratedToken;
        if (!string.IsNullOrEmpty(generated) && FixedTimeEquals(generated, supplied))
        {
            return new Caller("generated-superuser", McpCapability.All);
        }

        return null;
    }

    /// <summary>
    /// Reads the request body once to extract the JSON-RPC method, the tool
    /// name (when method == tools/call), and the request id. Returns the
    /// buffered body so the SDK can re-read it. Buffer cap = 64 KiB.
    /// </summary>
    private async Task<(string? ToolName, string? RequestId, Stream Buffered)> PeekToolCallAsync(HttpContext context)
    {
        const int cap = 64 * 1024;
        context.Request.EnableBuffering();
        var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cap, context.RequestAborted);
        buffer.Position = 0;
        context.Request.Body.Position = 0;

        string? toolName = null;
        string? requestId = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: context.RequestAborted);
            buffer.Position = 0;
            if (!doc.RootElement.TryGetProperty("method", out var methodEl)) return (null, null, buffer);
            var method = methodEl.GetString();
            if (!string.Equals(method, "tools/call", StringComparison.Ordinal)) return (null, null, buffer);

            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                requestId = idEl.GetString();

            if (doc.RootElement.TryGetProperty("params", out var paramsEl) &&
                paramsEl.ValueKind == JsonValueKind.Object &&
                paramsEl.TryGetProperty("name", out var nameEl))
            {
                toolName = nameEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Body isn't valid JSON-RPC — let the SDK error out.
        }

        return (toolName, requestId, buffer);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    private static async Task Reject(HttpContext context, int status, string error, string description)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.WWWAuthenticate =
            $"Bearer realm=\"mcp\", error=\"{error}\", error_description=\"{description}\"";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"error\":\"{error}\",\"error_description\":\"{description.Replace("\"", "\\\"")}\"}}");
    }
}
