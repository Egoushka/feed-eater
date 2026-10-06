using FeedEater.Storage;

namespace FeedEater.Ui;

public static class UiGuard
{
    public static bool IsHttps(HttpRequest request) =>
        request.IsHttps || string.Equals(request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Origin header, else Referer, must name this request's own host and port. Neither present counts as a mismatch.</summary>
    public static bool SameOrigin(HttpRequest request)
    {
        var source = request.Headers.Origin.ToString();
        if (source.Length == 0)
        {
            source = request.Headers.Referer.ToString();
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var port = request.Host.Port ?? (IsHttps(request) ? 443 : 80);
        return string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == port;
    }
}

public static class UiEndpoints
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    /// <summary>
    /// The server-rendered UI under /ui. Everything but the login page and the stylesheet needs the session cookie;
    /// every POST also needs a same-origin Origin or Referer and the per-session anti-forgery value.
    /// With no Mcp:Token the whole UI answers 503.
    /// </summary>
    public static IEndpointRouteBuilder MapFeedEaterUi(this IEndpointRouteBuilder app)
    {
        var session = app.ServiceProvider.GetRequiredService<UiSession>();
        var h = app.ServiceProvider.GetRequiredService<UiHandlers>();

        var ui = app.MapGroup("/ui");
        ui.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers.CacheControl = "no-store";
            return session.Enabled
                ? await next(context)
                : Results.Text("The UI is off: Mcp:Token is not set.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);
        });

        ui.MapGet("/login", h.LoginForm);
        ui.MapPost("/login", h.LoginAsync);
        ui.MapGet("/app.css", UiHandlers.Stylesheet);

        var secured = ui.MapGroup("");
        secured.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var cookie = http.Request.Cookies[UiSession.CookieName];
            if (!session.IsValid(cookie))
            {
                return UiHandlers.SeeOther(http, "/ui/login");
            }

            if (HttpMethods.IsPost(http.Request.Method))
            {
                if (!UiGuard.SameOrigin(http.Request) || !http.Request.HasFormContentType
                    || !session.CheckAntiForgery(cookie!, (await http.Request.ReadFormAsync(http.RequestAborted))["_csrf"]))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            return await next(context);
        });

        secured.MapGet("", h.TodayAsync);
        secured.MapGet("/posts", h.PostsAsync);
        secured.MapGet("/feedback", h.FeedbackAsync);
        secured.MapGet("/search", h.SearchAsync);
        secured.MapGet("/item/{id:long}", h.ItemAsync);
        secured.MapGet("/digests", h.DigestsAsync);
        secured.MapGet("/digest/{date}", h.DigestAsync);
        secured.MapGet("/weekly", (HttpContext c, CancellationToken t) => h.WeeklyAsync(c, null, t));
        secured.MapGet("/weekly/{date}", h.WeeklyAsync);
        secured.MapGet("/sources", h.SourcesAsync);
        secured.MapGet("/releases", h.ReleasesAsync);
        secured.MapGet("/ideas", h.IdeasAsync);
        secured.MapGet("/usage", h.UsageAsync);
        secured.MapPost("/vote", h.VoteAsync);
        secured.MapPost("/feeds/mute", h.MuteFeedAsync);
        secured.MapPost("/digest/run", h.RunDigestAsync);
        secured.MapPost("/logout", UiHandlers.Logout);
        return app;
    }
}
