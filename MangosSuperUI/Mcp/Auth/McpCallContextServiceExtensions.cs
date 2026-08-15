using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MangosSuperUI.Mcp.Auth;

/// <summary>
/// DI glue for <see cref="McpCallContext"/>. Registered as scoped so each
/// tool invocation gets the context that <see cref="McpAuthMiddleware"/>
/// stamped on the current HttpContext. When resolved outside an HTTP
/// request (background services, startup work) the empty instance is
/// returned so audit rows still get written — they just lack attribution.
/// </summary>
public static class McpCallContextServiceExtensions
{
    public static IServiceCollection AddMcpCallContext(this IServiceCollection services)
    {
        services.AddScoped<McpCallContext>(sp =>
        {
            var accessor = sp.GetService<IHttpContextAccessor>();
            var http = accessor?.HttpContext;
            if (http is null) return McpCallContext.Empty;

            if (http.Items.TryGetValue(McpCallContext.HttpContextKey, out var v) && v is McpCallContext ctx)
                return ctx;

            // Items may not be set if a request hit a non-/mcp endpoint — return empty.
            return McpCallContext.Empty;
        });
        return services;
    }
}
