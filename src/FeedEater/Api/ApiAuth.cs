using System.Threading.RateLimiting;

namespace FeedEater.Api;

/// <summary>
/// Rate limits for the API: 120 requests a minute per token. Requests that fail the checks before a token is known share one
/// partition of 20 a minute, so guessing tokens is slow however many different ones are tried.
/// </summary>
public sealed class ApiRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(
        key, _ => new FixedWindowRateLimiterOptions { PermitLimit = key == Anonymous ? 20 : 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    private const string Anonymous = "anonymous";

    /// <summary>True once the anonymous budget is spent. Reads the window without spending a permit.</summary>
    public bool AnonymousExhausted => limiter.GetStatistics(Anonymous)?.CurrentAvailablePermits == 0;

    public bool TryAcquire(long? tokenId) => limiter.AttemptAcquire(tokenId is { } id ? "token:" + id : Anonymous).IsAcquired;

    public void Dispose() => limiter.Dispose();
}

/// <summary>
/// Browsers are refused, the bearer token is checked, then the per-token rate limit. No problem body repeats the token.
/// Only 401 refusals spend the anonymous budget. Once it is spent a request gets 429 before any database lookup, unless its token
/// verified in the last 10 minutes: guessing stays slow and Postgres is shielded, and the owner's phone keeps working.
/// </summary>
public sealed class ApiAuth(ApiTokenStore tokens, ApiRateLimiter limits) : IEndpointFilter
{
    private const string Scheme = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.Headers.ContainsKey("Origin"))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Browsers may not call the API");
        }

        var header = http.Request.Headers.Authorization.ToString();
        var wellFormed = header.StartsWith(Scheme, StringComparison.Ordinal) && header.Length > Scheme.Length && header[Scheme.Length] != ' ';
        if (limits.AnonymousExhausted && !(wellFormed && tokens.VerifiedLately(header[Scheme.Length..])))
        {
            return TooMany();
        }

        if (!wellFormed)
        {
            return Refuse(StatusCodes.Status401Unauthorized, "A bearer token is required");
        }

        var row = await tokens.VerifyAsync(header[Scheme.Length..], http.RequestAborted);
        if (row is null || !row.Scopes.Contains(ApiScopes.App))
        {
            return Refuse(StatusCodes.Status401Unauthorized, "Token not accepted");
        }

        if (!limits.TryAcquire(row.Id))
        {
            return TooMany();
        }

        http.Items["api-token"] = row;
        return await next(context);

        IResult Refuse(int status, string title) => limits.TryAcquire(null)
            ? Results.Problem(statusCode: status, title: title)
            : TooMany();

        static IResult TooMany() => Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests");
    }
}

/// <summary>Endpoint metadata saying the route is behind <see cref="ApiAuth"/>; only <see cref="ApiAuthExtensions.RequireApiToken"/> adds it, with the filter.</summary>
public sealed class ApiTokenRequired;

public static class ApiAuthExtensions
{
    public static RouteGroupBuilder RequireApiToken(this RouteGroupBuilder group) =>
        group.AddEndpointFilter<RouteGroupBuilder, ApiAuth>().WithMetadata(new ApiTokenRequired());
}

/// <summary>An unhandled exception becomes a 500 problem without the exception text. A body over 64 KB is a 413 whatever the server enforces.</summary>
public sealed class ApiErrors(ILogger<ApiErrors> logger) : IEndpointFilter
{
    public const int MaxBodyBytes = 65_536;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Request.ContentLength > MaxBodyBytes)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The request body is too large");
        }

        try
        {
            return await next(context);
        }
        catch (BadHttpRequestException e)
        {
            // An oversized body surfaces here as 413.
            return Results.Problem(statusCode: e.StatusCode, title: "The request was refused");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "API request failed: {Path}", context.HttpContext.Request.Path);
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Something went wrong");
        }
    }
}
