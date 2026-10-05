using System.Security.Cryptography;
using System.Text;

namespace FeedEater.Mcp;

public static class McpExtensions
{
    public static IServiceCollection AddFeedEaterMcp(this IServiceCollection services)
    {
        // Stateless: no session, no GET stream.
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FeedTools>();
        return services;
    }

    /// <summary>
    /// Bearer token on every request, fail closed when none is configured, and no Origin header: a browser always
    /// sends one, so this is the DNS-rebinding guard.
    /// </summary>
    public static IEndpointRouteBuilder MapFeedEaterMcp(this IEndpointRouteBuilder app, string? token)
    {
        var expected = Encoding.UTF8.GetBytes($"Bearer {token}");
        app.MapMcp("/mcp").Add(builder =>
        {
            var next = builder.RequestDelegate!;
            builder.RequestDelegate = context =>
            {
                if (context.Request.Headers.ContainsKey("Origin"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                var given = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
                if (string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(given, expected))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                return next(context);
            };
        });
        return app;
    }
}
