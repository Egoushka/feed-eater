using System.Reflection;
using System.Text;
using FeedEater.Ui;

namespace FeedEater.Swipe;

public sealed record NextBody(string? Csrf, int? Position);

public sealed record ActBody(string? Csrf, long Session, int Position, string? Act);

public sealed record EventBody(string? Csrf, long Session, int Position, long? ShownAt, long? DwellMs, string? Direction);

public sealed record FeltBody(string? Csrf, long Session, int Felt);

public sealed record EndBody(string? Csrf, long Session);

public static class SwipeEndpoints
{
    /// <summary>Scripts only from this origin, nothing inline, no other host. The /ui policy is not loosened; this one is for /swipe alone.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; manifest-src 'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";

    private static readonly Dictionary<string, (string Resource, string Type)> Assets = new()
    {
        ["/app.js"] = ("FeedEater.Ui.swipe.js", "text/javascript; charset=utf-8"),
        ["/app.css"] = ("FeedEater.Ui.swipe.css", "text/css; charset=utf-8"),
        ["/manifest.webmanifest"] = ("FeedEater.Ui.swipe.webmanifest", "application/manifest+json; charset=utf-8"),
        ["/sw.js"] = ("FeedEater.Ui.swipe-sw.js", "text/javascript; charset=utf-8"),
        ["/icon.svg"] = ("FeedEater.Ui.swipe-icon.svg", "image/svg+xml"),
    };

    /// <summary>
    /// The phone page at /swipe. Same cookie session as /ui; every POST also needs a same-origin Origin or Referer and the per-session
    /// anti-forgery value, here in the JSON body because <c>sendBeacon</c> cannot set headers. Cards are rendered with <c>textContent</c> only.
    /// </summary>
    public static IEndpointRouteBuilder MapFeedEaterSwipe(this IEndpointRouteBuilder app)
    {
        var session = app.ServiceProvider.GetRequiredService<UiSession>();
        var swipe = app.MapGroup("/swipe");
        swipe.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers.CacheControl = "no-store";
            return session.Enabled
                ? await next(context)
                : Results.Text("The UI is off: Mcp:Token is not set.", "text/plain", Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);
        });

        foreach (var (path, (resource, type)) in Assets)
        {
            var text = new Lazy<string>(() => ReadResource(resource));
            swipe.MapGet(path, () => Results.Text(text.Value, type, Encoding.UTF8));
        }

        var secured = swipe.MapGroup("");
        secured.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (session.IsValid(http.Request.Cookies[UiSession.CookieName]))
            {
                return await next(context);
            }

            return HttpMethods.IsGet(http.Request.Method) ? Results.Redirect("/ui/login") : Results.StatusCode(StatusCodes.Status401Unauthorized);
        });

        secured.MapGet("", (HttpContext c) => Results.Text(Shell(session.AntiForgery(c.Request.Cookies[UiSession.CookieName]!)), "text/html", Encoding.UTF8));
        secured.MapPost("/next", async (NextBody b, HttpContext c, SwipeService svc, CancellationToken ct) =>
            Allowed(c, session, b.Csrf) ? Results.Json(await svc.NextAsync(b.Position, ct)) : Results.StatusCode(StatusCodes.Status403Forbidden));
        secured.MapPost("/act", async (ActBody b, HttpContext c, SwipeService svc, CancellationToken ct) =>
            Allowed(c, session, b.Csrf) ? Results.Json(await svc.ActAsync(b.Session, b.Position, b.Act ?? "", ct)) : Results.StatusCode(StatusCodes.Status403Forbidden));
        secured.MapPost("/event", async (EventBody b, HttpContext c, SwipeService svc, CancellationToken ct) =>
            Allowed(c, session, b.Csrf) ? Results.Json(new { ok = await svc.EventAsync(b.Session, b.Position, b.ShownAt, b.DwellMs, b.Direction, ct) }) : Results.StatusCode(StatusCodes.Status403Forbidden));
        secured.MapPost("/felt", async (FeltBody b, HttpContext c, SwipeService svc, CancellationToken ct) =>
            Allowed(c, session, b.Csrf) ? Results.Json(new { ok = await svc.FeltAsync(b.Session, b.Felt, ct) }) : Results.StatusCode(StatusCodes.Status403Forbidden));
        secured.MapPost("/end", async (EndBody b, HttpContext c, SwipeService svc, CancellationToken ct) =>
            Allowed(c, session, b.Csrf) ? Results.Json(await svc.EndAsync(b.Session, ct) ?? new NextResult("closed", Closed: new ClosedView(null, "empty"))) : Results.StatusCode(StatusCodes.Status403Forbidden));
        return app;
    }

    private static bool Allowed(HttpContext http, UiSession session, string? csrf) =>
        UiGuard.SameOrigin(http.Request) && http.Request.Cookies[UiSession.CookieName] is { } cookie && session.CheckAntiForgery(cookie, csrf);

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
    }

    private static string Shell(string csrf) =>
        $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <meta name="color-scheme" content="dark light"><meta name="theme-color" content="#14161a"><meta name="referrer" content="same-origin">
        <meta name="csrf" content="{Html.E(csrf)}"><link rel="manifest" href="/swipe/manifest.webmanifest"><link rel="icon" href="/swipe/icon.svg">
        <title>Swipe · feed-eater</title><link rel="stylesheet" href="/swipe/app.css"></head>
        <body>
        <main id="stage" aria-live="polite"><p class="empty">Loading…</p></main>
        <nav id="bar" aria-label="Card actions" hidden>
          <div data-for="item">
            <button type="button" data-do="dislike" aria-label="Not for me">👎</button>
            <button type="button" data-do="like" aria-label="Like">👍</button>
            <button type="button" data-do="save" aria-label="Save">📌</button>
            <button type="button" data-do="share" aria-label="Share">📤</button>
            <button type="button" data-do="idea" aria-label="File as an idea">💡</button>
            <button type="button" data-do="open" aria-label="Open the link">↗</button>
          </div>
          <div data-for="recall">
            <button type="button" class="wide" data-do="reveal">Show answer</button>
            <button type="button" class="wide" data-do="again" hidden>Again</button>
            <button type="button" class="wide" data-do="good" hidden>Good</button>
          </div>
          <div data-for="seed">
            <button type="button" class="wide" data-do="dismiss">Dismiss</button>
            <button type="button" class="wide" data-do="send">Send as an idea</button>
            <button type="button" data-do="open" aria-label="Open the source">↗</button>
          </div>
          <button type="button" data-do="end" aria-label="I am done">✕</button>
        </nav>
        <div id="toast" role="status"></div>
        <script src="/swipe/app.js" defer></script>
        </body></html>
        """;
}
