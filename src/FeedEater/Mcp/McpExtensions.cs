using Switchknife.McpGuard;

namespace FeedEater.Mcp;

public static class McpExtensions
{
    public static IServiceCollection AddFeedEaterMcp(this IServiceCollection services)
    {
        // Stateless: no session, no GET stream.
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FeedTools>().WithTools<PriorArtTools>();
        return services;
    }

    /// <summary>
    /// Bearer token on every request, fail closed when none is configured, and no Origin header: a browser always
    /// sends one, so this is the DNS-rebinding guard.
    /// </summary>
    public static IEndpointRouteBuilder MapFeedEaterMcp(this IEndpointRouteBuilder app, string? token)
    {
        app.MapMcp("/mcp").RequireMcpGuard(token);
        return app;
    }
}
