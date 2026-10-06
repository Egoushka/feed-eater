using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Fetch;
using FeedEater.Search;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Watch;

namespace FeedEater.Ui;

public sealed class UiHandlers(
    ItemStore items, DigestStore digests, FeedbackStore feedback, ProfileStore profiles, UsageStore usage, ArchiveSearch search,
    CallbackHandler callbacks, FeedDiscoverer discovery, WeeklyStore weekly, WatchSource watch, ReleaseStore releases, DigestTrigger trigger, UiSession session, LoginThrottle throttle,
    IOptions<FeedEaterOptions> options, TimeProvider time)
{
    private const int SearchLimit = 30;
    private const int PageSize = 30;
    private const int DigestListLimit = 90;
    private static readonly Lazy<string> Css = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FeedEater.Ui.app.css")!;
        return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
    });

    private FeedEaterOptions Settings => options.Value;

    public static IResult Stylesheet() => Results.Text(Css.Value, "text/css; charset=utf-8");

    public static IResult Html(string html, int status = 200) => Results.Text(html, "text/html; charset=utf-8", Encoding.UTF8, status);

    public static IResult SeeOther(HttpContext ctx, string url)
    {
        ctx.Response.Headers.Location = url;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    public IResult LoginForm() => Html(UiPages.Login(null));

    public async Task<IResult> LoginAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!UiGuard.SameOrigin(ctx.Request))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!throttle.TryAcquire())
        {
            return Html(UiPages.Login("Too many attempts. Wait a minute."), StatusCodes.Status429TooManyRequests);
        }

        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ct) : null;
        if (!session.CheckToken(form?["token"].ToString()))
        {
            return Html(UiPages.Login("Wrong token."), StatusCodes.Status401Unauthorized);
        }

        ctx.Response.Cookies.Append(UiSession.CookieName, session.IssueNew(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = UiGuard.IsHttps(ctx.Request),
            Path = "/ui",
            MaxAge = UiSession.Lifetime,
        });
        return SeeOther(ctx, "/ui");
    }

    public static IResult Logout(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(UiSession.CookieName, new CookieOptions { Path = "/ui" });
        return SeeOther(ctx, "/ui/login");
    }

    public async Task<IResult> TodayAsync(HttpContext ctx, CancellationToken ct)
    {
        var p = Context(ctx);
        var latest = (await digests.ListAsync(null, 1, ct)).FirstOrDefault();
        var today = trigger.Today();
        var pending = await trigger.PendingAsync(ct);
        var run = new RunPanel(
            Settings.RunJobs && Settings.Telegram.Token.Length > 0,
            latest is { Status: "sent" } && latest.LocalDate == today,
            pending is null ? null : pending.Resend ? "A send-again" : "A",
            await trigger.LastResultAsync(ct));
        return Html(UiPages.Today(p, latest, latest is null ? [] : await ShownAsync(latest, ct), await FiguresAsync(ct), run));
    }

    public async Task<IResult> DigestAsync(HttpContext ctx, string date, CancellationToken ct)
    {
        var p = Context(ctx);
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || await digests.GetAsync(date, ct) is not { } digest)
        {
            return NotFound(p);
        }

        return Html(UiPages.DigestPage(p, digest, await ShownAsync(digest, ct)));
    }

    public async Task<IResult> DigestsAsync(HttpContext ctx, CancellationToken ct) =>
        Html(UiPages.Digests(Context(ctx), await digests.ListAsync(null, DigestListLimit, ct)));

    public async Task<IResult> SearchAsync(HttpContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.Query;
        var q = new SearchQuery(
            Clip(query["q"].ToString().Trim(), 300), Blank(query["project"]), Blank(query["kind"]), Blank(query["from"]), Blank(query["to"]));
        var keys = (await profiles.AllAsync(ct)).Select(x => x.Key).ToList();
        IReadOnlyList<SearchHit>? hits = null;
        if (q.Text.Length > 0)
        {
            hits = await search.SearchAsync(q.Text, q.Project, q.Kind is "improve" or "new" or "fyi" ? q.Kind : null, Day(q.From, false), Day(q.To, true), SearchLimit, ct);
        }

        return Html(UiPages.Search(Context(ctx), q, keys, hits));
    }

    public async Task<IResult> PostsAsync(HttpContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.Query;
        var q = new PostsQuery(
            Blank(query["category"]),
            long.TryParse(query["feed"], NumberStyles.None, CultureInfo.InvariantCulture, out var feed) ? feed : null,
            Blank(query["project"]),
            query["kind"].ToString() is "improve" or "new" or "fyi" ? query["kind"].ToString() : null,
            query["unrated"] == "1",
            query["summary"] == "1",
            int.TryParse(query["days"], NumberStyles.None, CultureInfo.InvariantCulture, out var days) && PostsQuery.DayChoices.Contains(days) ? days : 7,
            query["muted"] == "1");
        var before = PageCursor.Parse(query["before"]);
        var rows = await items.PostsAsync(
            new PostFilter(time.GetUtcNow().AddDays(-q.Days), q.Category, q.Feed, q.Project, q.Kind, q.Unrated, q.Summary, q.ShowMuted), before, PageSize + 1, ct);
        var shown = rows.Take(PageSize).ToList();
        var next = rows.Count > PageSize ? PageCursor.Of(shown[^1].PublishedAt, shown[^1].Id).ToString() : null;
        var projects = (await profiles.AllAsync(ct)).Select(x => x.Key).ToList();
        return Html(UiPages.Posts(Context(ctx), q, await items.CategoriesAsync(ct), await items.FeedsAsync(ct), projects, await items.WithMembersAsync(shown, ct), next, before is not null));
    }

    public async Task<IResult> FeedbackAsync(HttpContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.Query;
        var tab = query["tab"].ToString() is "down" or "idea" ? query["tab"].ToString() : "up";
        var before = PageCursor.Parse(query["before"]);
        var rows = await items.RatedAsync(tab, before, PageSize + 1, ct);
        var shown = rows.Take(PageSize).ToList();
        var next = rows.Count > PageSize ? PageCursor.Of(shown[^1].RatedAt, shown[^1].Id).ToString() : null;
        var now = time.GetUtcNow();
        return Html(UiPages.Feedback(
            Context(ctx), tab, await feedback.TotalsAsync(ct),
            DigestStats.UpRate(await feedback.VotesSinceAsync(now.AddDays(-7), ct)), DigestStats.UpRate(await feedback.VotesSinceAsync(now.AddDays(-30), ct)),
            await items.WithMembersAsync(shown, ct), next, before is not null));
    }

    public async Task<IResult> ItemAsync(HttpContext ctx, long id, CancellationToken ct)
    {
        var p = Context(ctx);
        return await items.GetAsync(id, ct) is { } item ? Html(UiPages.Item(p, (await items.WithMembersAsync([item], ct))[0])) : NotFound(p);
    }

    public async Task<IResult> SourcesAsync(HttpContext ctx, CancellationToken ct)
    {
        var query = ctx.Request.Query;
        var sort = query["sort"].ToString() is "feed" or "items" or "perweek" or "candidates" or "shown" or "up" or "down" or "rate" or "flag" ? query["sort"].ToString() : "items";
        var desc = query["dir"].ToString() != "asc";
        var flaggedOnly = query["flag"].ToString() == "1";
        var rows = (await items.SourceStatsAsync(time.GetUtcNow().AddDays(-30), ct)).Select(s => new SourceRow(s, Flag(s))).ToList();
        if (flaggedOnly)
        {
            rows = rows.Where(r => r.Flag is not null).ToList();
        }

        return Html(UiPages.Sources(Context(ctx), SortSources(rows, sort, desc), sort, desc, flaggedOnly, await discovery.SuggestionsAsync(ct)));
    }

    public async Task<IResult> WeeklyAsync(HttpContext ctx, string? date, CancellationToken ct)
    {
        var p = Context(ctx);
        WeeklyRow? row;
        if (date is null)
        {
            row = await weekly.LatestAsync(ct);
        }
        else if (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && await weekly.GetAsync(date, ct) is { } found)
        {
            row = found;
        }
        else
        {
            return NotFound(p);
        }

        return Html(UiPages.Weekly(p, row, await weekly.ListAsync(52, ct)));
    }

    public async Task<IResult> ReleasesAsync(HttpContext ctx, CancellationToken ct) =>
        Html(UiPages.Releases(Context(ctx), await watch.LoadAsync(ct), await releases.ListAsync(500, ct)));

    public async Task<IResult> IdeasAsync(HttpContext ctx, CancellationToken ct) =>
        Html(UiPages.Ideas(Context(ctx), await feedback.IdeasAsync(null, 100, ct)));

    public async Task<IResult> UsageAsync(HttpContext ctx, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now.AddDays(-30);
        var month = await usage.SpendSinceAsync(DigestStats.MonthStart(now, Settings.Zone), ct);
        var days = await usage.SpendByDayAsync(since, Settings.TimeZone, ct);
        return Html(UiPages.Usage(Context(ctx), month, Settings.Llm.MonthlyBudget, days, await usage.SpendByPurposeAsync(since, ct), days.Sum(d => d.Cost)));
    }

    public async Task<IResult> VoteAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        if (!long.TryParse(form["item"], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return Results.BadRequest();
        }

        if (await items.GetAsync(id, ct) is null)
        {
            return NotFound(Context(ctx));
        }

        var back = SafeBack(form["back"]);
        switch (form["v"].ToString())
        {
            case "up":
                await callbacks.VoteAsync(id, 1, ct);
                return SeeOther(ctx, back);
            case "down":
                await callbacks.VoteAsync(id, -1, ct);
                return SeeOther(ctx, back);
            case "clear":
                await callbacks.ClearVoteAsync(id, ct);
                return SeeOther(ctx, back);
            case "save":
                var saved = await callbacks.SaveAsync(id, ct);
                return SeeOther(ctx, saved is SaveOutcome.Saved or SaveOutcome.AlreadySaved ? back : WithNotice(back, saved == SaveOutcome.NotConfigured ? "save-off" : saved == SaveOutcome.Refused ? "save-refused" : "save-down"));
            case "idea":
                var (project, _) = await callbacks.FileIdeaAsync(id, ct);
                return SeeOther(ctx, WithNotice(back, project is null ? "file-failed" : "filed"));
            default:
                return Results.BadRequest();
        }
    }

    public async Task<IResult> MuteFeedAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        if (!long.TryParse(form["feed"], NumberStyles.None, CultureInfo.InvariantCulture, out var feed) || form["mute"].ToString() is not ("0" or "1"))
        {
            return Results.BadRequest();
        }

        return await items.SetFeedMutedAsync(feed, form["mute"] == "1", ct) ? SeeOther(ctx, SafeBack(form["back"])) : NotFound(Context(ctx));
    }

    public async Task<IResult> RunDigestAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!Settings.RunJobs || Settings.Telegram.Token.Length == 0)
        {
            return SeeOther(ctx, "/ui?notice=digest-off");
        }

        var resend = (await ctx.Request.ReadFormAsync(ct))["mode"] == "resend";
        await trigger.RequestAsync(resend, ct);
        return SeeOther(ctx, resend ? "/ui?notice=queued-resend" : "/ui?notice=queued");
    }

    private async Task<IReadOnlyList<ItemView>> ShownAsync(DigestRow digest, CancellationToken ct)
    {
        var shown = new List<ItemView>();
        foreach (var id in digest.ItemIds)
        {
            if (await items.GetAsync(id, ct) is { Summary: not null } item)
            {
                shown.Add(item);
            }
        }

        return await items.WithMembersAsync(shown, ct);
    }

    private async Task<Figures> FiguresAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return new Figures(
            await usage.SpendSinceAsync(DigestStats.MonthStart(now, Settings.Zone), ct), Settings.Llm.MonthlyBudget,
            DigestStats.UpRate(await feedback.VotesSinceAsync(now.AddDays(-7), ct)), await feedback.VotesSinceAsync(now.AddDays(-1), ct));
    }

    private PageContext Context(HttpContext ctx)
    {
        var notice = ctx.Request.Query["notice"].ToString();
        return new PageContext(session.AntiForgery(ctx.Request.Cookies[UiSession.CookieName] ?? ""), Settings.Zone, notice.Length == 0 ? null : notice);
    }

    private IResult NotFound(PageContext p) => Html(UiPages.Message(p, "Not found", "There is nothing here."), StatusCodes.Status404NotFound);

    /// <summary>Never flags a feed that is fine: a feed with items that were shown and liked has no flag.</summary>
    private static string? Flag(SourceStats s) =>
        s.Items == 0 ? "no items" : s.Shown == 0 ? "never shown" : s.Up == 0 ? "never liked" : null;

    private static IReadOnlyList<SourceRow> SortSources(List<SourceRow> rows, string sort, bool desc)
    {
        if (sort == "feed")
        {
            return (desc ? rows.OrderByDescending(r => r.Stats.Title, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Stats.Title, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        double? Key(SourceRow r) => sort switch
        {
            "perweek" => r.Stats.PostsPerWeek,
            "candidates" => r.Stats.Candidates,
            "shown" => r.Stats.Shown,
            "up" => r.Stats.Up,
            "down" => r.Stats.Down,
            "rate" => r.Rate,
            "flag" => r.Flag is null ? 0 : r.Flag == "never liked" ? 1 : r.Flag == "never shown" ? 2 : 3,
            _ => r.Stats.Items,
        };

        var known = rows.Where(r => Key(r) is not null);
        var ordered = desc ? known.OrderByDescending(r => Key(r)) : known.OrderBy(r => Key(r));
        return [.. ordered.ThenBy(r => r.Stats.Title, StringComparer.OrdinalIgnoreCase), .. rows.Where(r => Key(r) is null)];
    }

    /// <summary>A same-site path under /ui; anything else (another host, a protocol-relative URL) falls back to /ui.</summary>
    internal static string SafeBack(string? back) =>
        back is { Length: > 0 and <= 600 } && back.StartsWith("/ui", StringComparison.Ordinal) && !back.StartsWith("//", StringComparison.Ordinal)
        && (back.Length == 3 || back[3] is '/' or '?' or '#') && back.All(c => c is >= ' ' and < '\u007f' && c != '\\')
            ? back
            : "/ui";

    private static string WithNotice(string back, string code)
    {
        var hash = back.IndexOf('#', StringComparison.Ordinal);
        var path = hash < 0 ? back : back[..hash];
        var fragment = hash < 0 ? "" : back[hash..];
        return $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}notice={code}{fragment}";
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : Clip(value.Trim(), 100);

    /// <summary>A yyyy-MM-dd date in the display zone: the start of that day, or of the next day when <paramref name="endOfDay"/> (the range is exclusive at the top).</summary>
    private DateTimeOffset? Day(string? text, bool endOfDay)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return null;
        }

        var local = d.AddDays(endOfDay ? 1 : 0).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Settings.Zone.GetUtcOffset(local));
    }
}
