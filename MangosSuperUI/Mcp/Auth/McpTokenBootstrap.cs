using System.Security.Cryptography;
using MangosSuperUI.Mcp.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Mcp.Auth;

/// <summary>
/// Startup hook for the MCP auth layer. Runs once at <see cref="StartAsync"/>
/// (before the first request). If bearer auth is required but no token is
/// configured anywhere (env var, allowlist, or already-generated), it
/// generates a cryptographically-random 256-bit token, logs it once at
/// <see cref="LogLevel.Warning"/> with a clear "SAVE THIS" message, and
/// stashes it in <see cref="McpAuthMiddleware.GeneratedToken"/> so the
/// middleware accepts it as a superuser fallback.
///
/// This keeps a fresh solo deployment usable without forcing the operator
/// to manually generate and paste a token before the very first request.
/// Disable by setting <c>MCP_REQUIRE_TOKEN=false</c> in the environment.
///
/// Re-runs are idempotent — once <see cref="McpAuthMiddleware.GeneratedToken"/>
/// is set (regardless of where it came from), the bootstrap is a no-op.
/// </summary>
public sealed class McpTokenBootstrap : IHostedService
{
    private readonly IOptionsMonitor<McpOptions> _options;
    private readonly ILogger<McpTokenBootstrap> _log;

    public McpTokenBootstrap(IOptionsMonitor<McpOptions> options, ILogger<McpTokenBootstrap> log)
    {
        _options = options;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;

        // 1. If the operator disabled auth, there's nothing to bootstrap.
        if (!opts.Auth.RequireBearerToken)
            return Task.CompletedTask;

        // 2. If a token has already been generated (e.g. by an earlier
        //    StartAsync in a test harness) we don't overwrite it.
        if (McpAuthMiddleware.GeneratedToken is { Length: > 0 } existing)
        {
            _log.LogDebug("MCP auth bootstrap: using pre-existing generated token ({Len} chars)", existing.Length);
            return Task.CompletedTask;
        }

        // 3. If the legacy env-var token is set, the middleware already
        //    picks it up — no need to generate one.
        var envToken = Environment.GetEnvironmentVariable(opts.Auth.EnvVarName);
        if (!string.IsNullOrEmpty(envToken))
        {
            _log.LogDebug("MCP auth bootstrap: {Env} env var is set; using it directly", opts.Auth.EnvVarName);
            return Task.CompletedTask;
        }

        // 4. If the structured allowlist has at least one token, also done.
        if (opts.Auth.Tokens is { Count: > 0 })
        {
            _log.LogDebug("MCP auth bootstrap: Mcp.Auth.Tokens has {Count} entries; using them", opts.Auth.Tokens.Count);
            return Task.CompletedTask;
        }

        // 5. Nothing configured → generate. 32 bytes = 256 bits of entropy,
        //    hex-encoded = 64 chars. Cryptographically random.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(bytes).ToLowerInvariant();
        McpAuthMiddleware.GeneratedToken = token;

        // Log loudly so an operator notices. Use Warning so it shows up
        // in default Information-level filters.
        _log.LogWarning(
            "===========================================================\n" +
            "  MCP endpoint requires bearer auth but no token was configured.\n" +
            "  A 256-bit superuser token has been auto-generated for this run:\n" +
            "\n" +
            "    MCP_AUTH_TOKEN={Token}\n" +
            "\n" +
            "  Use it as `Authorization: Bearer <token>` on every MCP call.\n" +
            "  It is NOT persisted — restart the container to roll a new one.\n" +
            "  Set MCP_AUTH_TOKEN explicitly to keep a stable token across restarts.\n" +
            "  Set MCP_REQUIRE_TOKEN=false to disable auth entirely (dev only).\n" +
            "===========================================================",
            token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
