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
/// Only refusals spend the anonymous budget. Once it is spent every request gets 429 before any database lookup, valid tokens
/// included: guessing stays slow and Postgres is shielded, at the price of locking the owner out for the rest of the minute.
/// </summary>
public sealed class ApiAuth(ApiTokenStore tokens, ApiRateLimiter limits) : IEndpointFilter
{
    private const string Scheme = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (limits.AnonymousExhausted)
        {
            return TooMany();
        }

        if (http.Request.Headers.ContainsKey("Origin"))
        {
            return Refuse(StatusCodes.Status403Forbidden, "Browsers may not call the API");
        }

        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.Ordinal) || header.Length == Scheme.Length || header[Scheme.Length] == ' ')
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

/// <summary>An unhandled exception becomes a 500 problem without the exception text.</summary>
public sealed class ApiErrors(ILogger<ApiErrors> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
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
