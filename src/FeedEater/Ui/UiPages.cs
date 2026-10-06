using System.Globalization;
using System.Text;
using FeedEater.Digest;
using FeedEater.Storage;
using FeedEater.Watch;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

/// <summary>What every page needs: the anti-forgery value for its forms, the display zone and an optional fixed notice.</summary>
public sealed record PageContext(string Csrf, TimeZoneInfo Zone, string? Notice);

public sealed record Figures(decimal MonthSpend, decimal Budget, double? WeekUpRate, VoteCounts Yesterday);

/// <summary>The /ui/posts filters as the page shows them; <see cref="Url"/> is the only way a filtered link is built, so nothing raw is reflected.</summary>
public sealed record PostsQuery(string? Category, long? Feed, string? Project, string? Kind, bool Unrated, bool Summary, int Days, bool ShowMuted = false)
{
    public static readonly int[] DayChoices = [1, 7, 30, 90];

    public string Url(string? before = null)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        Add("category", Category);
        Add("feed", Feed?.ToString(CultureInfo.InvariantCulture));
        Add("project", Project);
        Add("kind", Kind);
        Add("unrated", Unrated ? "1" : null);
        Add("summary", Summary ? "1" : null);
        Add("muted", ShowMuted ? "1" : null);
        Add("days", Days == 7 ? null : Days.ToString(CultureInfo.InvariantCulture));
        Add("before", before);
        return parts.Count == 0 ? "/ui/posts" : "/ui/posts?" + string.Join('&', parts);
    }
}

public sealed record RunPanel(bool Enabled, bool Sent, string? Pending, ForceResult? Last);

public sealed record SourceRow(SourceStats Stats, string? Flag)
{
    public double? Rate => Stats.Up + Stats.Down == 0 ? null : (double)Stats.Up / (Stats.Up + Stats.Down);
}

/// <summary>
/// Server-rendered pages. Every dynamic value goes through <see cref="Html.E"/>; links to feed URLs go through
/// <see cref="Html.External"/>. There is no script, so the page needs no inline handler and the CSP allows none.
/// </summary>
public static class UiPages
{
    private static readonly (string Path, string Label)[] Nav =
    [
        ("/ui", "Today"), ("/ui/posts", "Posts"), ("/ui/feedback", "Feedback"), ("/ui/search", "Search"), ("/ui/digests", "Digests"), ("/ui/weekly", "Weekly"),
        ("/ui/sources", "Sources"), ("/ui/releases", "Releases"), ("/ui/ideas", "Ideas"), ("/ui/usage", "Usage"),
    ];

    private static readonly Dictionary<string, string> Notices = new()
    {
        ["filed"] = "Filed in Plane.",
        ["file-failed"] = "Not filed: Plane did not accept it, or the item has no suggestion.",
        ["queued"] = "Digest queued; it starts within a minute.",
        ["queued-resend"] = "Queued: today's digest will be sent again within a minute.",
        ["saved"] = "Saved to Karakeep.",
        ["save-down"] = "Karakeep is not reachable; nothing was saved. Try again later.",
        ["save-off"] = "Karakeep is not configured, so nothing was saved.",
        ["save-refused"] = "This item has no web link to save.",
        ["digest-off"] = "The digest job is off: Telegram is not configured.",
    };

    public static string Login(string? error)
    {
        var body = new StringBuilder("<main id=\"main\" class=\"narrow\"><h1>feed-eater</h1>");
        if (error is not null)
        {
            body.Append($"<p class=\"notice bad\" role=\"alert\">{E(error)}</p>");
        }

        body.Append(
            """
            <form method="post" action="/ui/login" class="stack">
              <label for="token">Access token</label>
              <input id="token" name="token" type="password" autocomplete="current-password" required autofocus>
              <button type="submit" class="primary">Sign in</button>
            </form></main>
            """);
        return Shell("Sign in", body.ToString());
    }

    public static string Message(PageContext p, string title, string text) =>
        Layout(p, title, null, $"<h1>{E(title)}</h1><p>{E(text)}</p>");

    public static string Today(PageContext p, DigestRow? digest, IReadOnlyList<ItemView> items, Figures figures, RunPanel run)
    {
        var h = new StringBuilder();
        h.Append("<h1>Today</h1>");
        Brief(h, items);
        Stats(h, figures);
        RunControls(h, p, run);
        if (digest is null)
        {
            h.Append("<p class=\"empty\">No digest yet.</p>");
        }
        else
        {
            DigestBody(h, p, digest, items, "/ui");
        }

        return Layout(p, "Today", "/ui", h.ToString());
    }

    private static void Brief(StringBuilder h, IReadOnlyList<ItemView> items)
    {
        var lines = DigestBrief.Build(items);
        if (lines.Count == 0)
        {
            return;
        }

        var ideas = items.Count(i => i.Suggestion is not null);
        h.Append($"<section class=\"panel brief\" aria-labelledby=\"brief-h\"><h2 id=\"brief-h\">The day in brief</h2><p class=\"meta\">{N(items.Count)} highlights, {N(ideas)} with a suggestion</p><ul>");
        foreach (var l in lines)
        {
            h.Append($"<li><a href=\"#item-{N(l.ItemId)}\"><span class=\"badge\">{E(l.Project)}</span></a> {(l.Count > 1 ? $"<span class=\"muted\">{N(l.Count)} items</span> " : "")}{E(l.Text)}</li>");
        }

        h.Append("</ul></section>");
    }

    public static string Posts(
        PageContext p, PostsQuery q, IReadOnlyList<string> categories, IReadOnlyList<Feed> feeds, IReadOnlyList<string> projects,
        IReadOnlyList<ItemView> posts, string? nextBefore, bool paged)
    {
        var h = new StringBuilder("<h1>Posts</h1>");
        var active = new[] { q.Category is not null, q.Feed is not null, q.Project is not null, q.Kind is not null, q.Unrated, q.Summary, q.ShowMuted, q.Days != 7 }.Count(x => x);
        var badge = active > 0 ? $" <span class=\"badge\">{N(active)} active</span>" : "";
        h.Append($"<details class=\"filterbox\"><summary>Filters{badge}</summary>");
        h.Append(
            $"""
            <form method="get" action="/ui/posts" class="filters">
              <div class="field"><label for="category">Category</label><select id="category" name="category">{Option("", "any", q.Category)}{string.Concat(categories.Select(c => Option(c, c, q.Category)))}</select></div>
              <div class="field"><label for="feed">Feed</label><select id="feed" name="feed">{Option("", "any", q.Feed?.ToString(CultureInfo.InvariantCulture))}{string.Concat(feeds.Select(f => Option(N(f.Id), f.Title, q.Feed?.ToString(CultureInfo.InvariantCulture))))}</select></div>
              <div class="field"><label for="project">Project or topic</label><select id="project" name="project">{Option("", "any", q.Project)}{string.Concat(projects.Select(k => Option(k, k, q.Project)))}</select></div>
              <div class="field"><label for="kind">Kind</label><select id="kind" name="kind">{Option("", "any", q.Kind)}{Option("improve", "improve", q.Kind)}{Option("new", "new", q.Kind)}{Option("fyi", "fyi", q.Kind)}</select></div>
              <div class="field"><label for="days">Published in the last</label><select id="days" name="days">{string.Concat(PostsQuery.DayChoices.Select(d => Option(N(d), d == 1 ? "day" : N(d) + " days", N(q.Days))))}</select></div>
              <div class="checks">
                <label class="check"><input type="checkbox" name="unrated" value="1"{(q.Unrated ? " checked" : "")}> Unrated only</label>
                <label class="check"><input type="checkbox" name="summary" value="1"{(q.Summary ? " checked" : "")}> With AI summary only</label>
                <label class="check"><input type="checkbox" name="muted" value="1"{(q.ShowMuted ? " checked" : "")}> Show muted feeds</label>
              </div>
              <button type="submit" class="primary">Apply</button>
            </form></details>
            """);
        CardList(h, p, posts, q.Url(), "No posts match these filters.");
        h.Append("<p class=\"pager\">");
        if (paged)
        {
            h.Append($"<a href=\"{E(q.Url())}\">Back to the newest</a> ");
        }

        if (nextBefore is not null)
        {
            h.Append($"<a rel=\"next\" href=\"{E(q.Url(nextBefore))}\">Older posts</a>");
        }

        h.Append("</p>");
        return Layout(p, "Posts", "/ui/posts", h.ToString());
    }

    public static string Feedback(
        PageContext p, string tab, RatingTotals totals, double? rate7, double? rate30, IReadOnlyList<ItemView> cards, string? nextBefore, bool paged)
    {
        var h = new StringBuilder("<h1>Feedback</h1>");
        h.Append("<dl class=\"stats\">")
            .Append($"<div><dt>7-day 👍 rate</dt><dd>{Percent(rate7)}</dd></div>")
            .Append($"<div><dt>30-day 👍 rate</dt><dd>{Percent(rate30)}</dd></div></dl>");
        h.Append("<nav class=\"tabs\" aria-label=\"Rating\">");
        foreach (var (key, label, count) in new[] { ("up", "👍 Liked", totals.Up), ("down", "👎 Disliked", totals.Down), ("idea", "💡 Filed ideas", totals.Ideas) })
        {
            h.Append($"<a href=\"/ui/feedback?tab={key}\"{(key == tab ? " aria-current=\"page\"" : "")}>{label} <span class=\"count\">{N(count)}</span></a>");
        }

        h.Append("</nav>");
        var url = $"/ui/feedback?tab={tab}";
        CardList(h, p, cards, url, "Nothing here yet.");
        h.Append("<p class=\"pager\">");
        if (paged)
        {
            h.Append($"<a href=\"{url}\">Back to the newest</a> ");
        }

        if (nextBefore is not null)
        {
            h.Append($"<a rel=\"next\" href=\"{E(url)}&amp;before={E(nextBefore)}\">Older</a>");
        }

        h.Append("</p>");
        return Layout(p, "Feedback", "/ui/feedback", h.ToString());
    }

    private static void CardList(StringBuilder h, PageContext p, IReadOnlyList<ItemView> cards, string back, string empty)
    {
        if (cards.Count == 0)
        {
            h.Append($"<p class=\"empty\">{E(empty)}</p>");
            return;
        }

        h.Append("<ol class=\"cards\">");
        foreach (var v in cards)
        {
            h.Append("<li>");
            Card(h, p, v, back, full: false);
            h.Append("</li>");
        }

        h.Append("</ol>");
    }

    public static string DigestPage(PageContext p, DigestRow digest, IReadOnlyList<ItemView> items) =>
        Layout(p, $"Digest {digest.LocalDate}", "/ui/digests", DigestHtml(p, digest, items, $"/ui/digest/{digest.LocalDate}"));

    private static string DigestHtml(PageContext p, DigestRow digest, IReadOnlyList<ItemView> items, string back)
    {
        var h = new StringBuilder($"<h1>Digest {E(digest.LocalDate)}</h1>");
        DigestBody(h, p, digest, items, back);
        return h.ToString();
    }

    private static void DigestBody(StringBuilder h, PageContext p, DigestRow digest, IReadOnlyList<ItemView> items, string back)
    {
        h.Append($"<p class=\"meta\"><span class=\"badge {StatusClass(digest.Status)}\">{E(digest.Status)}</span> ")
            .Append(N(items.Count)).Append(" shown of ").Append(N(digest.Candidates)).Append(" new items, ")
            .Append(N(digest.Triaged)).Append(" triaged");
        if (digest.SentAt is { } sent)
        {
            h.Append(" · sent ").Append(E(Local(p, sent)));
        }

        h.Append("</p>");
        foreach (var line in (digest.Note ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            h.Append($"<p class=\"notice warn\">{E(line)}</p>");
        }

        if (digest.Error is not null && digest.Status != "sent")
        {
            h.Append($"<p class=\"notice bad\">Last error: {E(digest.Error)}</p>");
        }

        if (items.Count == 0)
        {
            h.Append("<p class=\"empty\">No highlights in this digest.</p>");
            return;
        }

        h.Append("<ol class=\"cards\">");
        foreach (var v in items)
        {
            h.Append("<li>");
            Card(h, p, v, back, full: false);
            h.Append("</li>");
        }

        h.Append("</ol>");
    }

    public static string Search(PageContext p, SearchQuery q, IReadOnlyList<string> projects, IReadOnlyList<SearchHit>? hits)
    {
        var h = new StringBuilder("<h1>Search</h1>");
        h.Append(
            $"""
            <form method="get" action="/ui/search" class="filters">
              <div class="field wide"><label for="q">Query</label><input id="q" name="q" type="search" value="{E(q.Text)}" maxlength="300"></div>
              <div class="field"><label for="project">Project or topic</label><select id="project" name="project">{Option("", "any", q.Project)}{string.Concat(projects.Select(k => Option(k, k, q.Project)))}</select></div>
              <div class="field"><label for="kind">Kind</label><select id="kind" name="kind">{Option("", "any", q.Kind)}{Option("improve", "improve", q.Kind)}{Option("new", "new", q.Kind)}{Option("fyi", "fyi", q.Kind)}</select></div>
              <div class="field"><label for="from">From</label><input id="from" name="from" type="date" value="{E(q.From)}"></div>
              <div class="field"><label for="to">To</label><input id="to" name="to" type="date" value="{E(q.To)}"></div>
              <button type="submit" class="primary">Search</button>
            </form>
            """);
        if (hits is null)
        {
            h.Append("<p class=\"empty\">Type a query to search the archive by meaning and keywords.</p>");
        }
        else if (hits.Count == 0)
        {
            h.Append("<p class=\"empty\">Nothing found.</p>");
        }
        else
        {
            h.Append($"<p class=\"meta\">{N(hits.Count)} results</p><ol class=\"results\">");
            foreach (var hit in hits)
            {
                h.Append("<li><div class=\"result\">")
                    .Append($"<a class=\"title\" href=\"/ui/item/{N(hit.Id)}\">{E(hit.Title)}</a>")
                    .Append("<p class=\"meta\">").Append(Meta(p, hit.Feed, null, hit.Project, hit.Kind, hit.PublishedAt));
                if (hit.Vote is { } vote)
                {
                    h.Append(" · ").Append(vote > 0 ? "👍" : "👎");
                }

                if (hit.Also > 0)
                {
                    h.Append($" · +{N(hit.Also)} similar");
                }

                h.Append("</p>");
                if (hit.Summary is not null)
                {
                    h.Append($"<p>{E(hit.Summary)}</p>");
                }

                h.Append("</div></li>");
            }

            h.Append("</ol>");
        }

        return Layout(p, "Search", "/ui/search", h.ToString());
    }

    public static string Item(PageContext p, ItemView v)
    {
        var h = new StringBuilder();
        Card(h, p, v, $"/ui/item/{N(v.Id)}", full: true);
        if (v.Relevance is { } relevance)
        {
            h.Append($"<p class=\"meta\">Triage: relevance {N(relevance)}{(string.IsNullOrEmpty(v.Reason) ? "" : " · " + E(v.Reason))}</p>");
        }

        h.Append("<h2>Text</h2>");
        h.Append(v.Content.Length == 0 ? "<p class=\"empty\">No text stored.</p>" : $"<pre class=\"content\">{E(v.Content)}</pre>");
        return Layout(p, v.Title, "/ui/search", h.ToString());
    }

    public static string Digests(PageContext p, IReadOnlyList<DigestRow> rows)
    {
        var h = new StringBuilder("<h1>Digests</h1>");
        if (rows.Count == 0)
        {
            h.Append("<p class=\"empty\">No digests yet.</p>");
        }
        else
        {
            h.Append("<div class=\"scroll\"><table><thead><tr><th>Date</th><th>Status</th><th class=\"num\">Shown</th><th class=\"num\">Candidates</th><th class=\"num\">Triaged</th><th>Sent</th></tr></thead><tbody>");
            foreach (var d in rows)
            {
                h.Append($"<tr><td><a href=\"/ui/digest/{E(d.LocalDate)}\">{E(d.LocalDate)}</a></td>")
                    .Append($"<td><span class=\"badge {StatusClass(d.Status)}\">{E(d.Status)}</span></td>")
                    .Append($"<td class=\"num\">{N(d.ItemIds.Length)}</td><td class=\"num\">{N(d.Candidates)}</td><td class=\"num\">{N(d.Triaged)}</td>")
                    .Append($"<td>{(d.SentAt is { } at ? E(Local(p, at)) : "–")}</td></tr>");
            }

            h.Append("</tbody></table></div>");
        }

        return Layout(p, "Digests", "/ui/digests", h.ToString());
    }

    public static string Sources(PageContext p, IReadOnlyList<SourceRow> rows, string sort, bool desc, bool flaggedOnly)
    {
        var h = new StringBuilder("<h1>Sources</h1><p class=\"meta\">Items published in the last 30 days, per Miniflux feed. Muting a feed keeps it ingested and searchable but leaves it out of digests and the Today brief.</p>");
        h.Append(flaggedOnly
            ? "<p><a href=\"" + E(SourcesPath(sort, desc, false)) + "\">Show all feeds</a></p>"
            : "<p><a href=\"" + E(SourcesPath(sort, desc, true)) + "\">Show only flagged feeds</a></p>");
        if (rows.Count == 0)
        {
            h.Append("<p class=\"empty\">No feeds.</p>");
            return Layout(p, "Sources", "/ui/sources", h.ToString());
        }

        h.Append("<div class=\"scroll\"><table><thead><tr>");
        foreach (var (key, label, numeric) in new[]
        {
            ("feed", "Feed", false), ("items", "Items", true), ("perweek", "Posts/week", true), ("candidates", "Candidates", true), ("shown", "Shown", true),
            ("up", "👍", true), ("down", "👎", true), ("rate", "👍 rate", true), ("flag", "Flag", false),
        })
        {
            var current = key == sort;
            var aria = current ? (desc ? " aria-sort=\"descending\"" : " aria-sort=\"ascending\"") : "";
            var nextDesc = current ? !desc : numeric;
            h.Append($"<th{aria}{(numeric ? " class=\"num\"" : "")}><a href=\"{E(SourcesPath(key, nextDesc, flaggedOnly))}\">{E(label)}</a></th>");
        }

        h.Append("<th><span class=\"visually-hidden\">Mute</span></th></tr></thead><tbody>");
        var back = SourcesPath(sort, desc, flaggedOnly);
        foreach (var r in rows)
        {
            var s = r.Stats;
            h.Append($"<tr><td>{E(s.Title)}{(string.IsNullOrEmpty(s.Category) ? "" : $" <span class=\"muted\">{E(s.Category)}</span>")}{(s.Muted ? " <span class=\"badge\">muted</span>" : "")}</td>")
                .Append($"<td class=\"num\">{N(s.Items)}</td><td class=\"num\">{s.PostsPerWeek.ToString("0.0", CultureInfo.InvariantCulture)}</td><td class=\"num\">{N(s.Candidates)}</td><td class=\"num\">{N(s.Shown)}</td>")
                .Append($"<td class=\"num\">{N(s.Up)}</td><td class=\"num\">{N(s.Down)}</td><td class=\"num\">{Percent(r.Rate)}</td>")
                .Append($"<td>{(r.Flag is null ? "" : $"<span class=\"badge warn\">{E(r.Flag)}</span>")}</td>")
                .Append($"<td><form method=\"post\" action=\"/ui/feeds/mute\" class=\"inline\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(p.Csrf)}\"><input type=\"hidden\" name=\"feed\" value=\"{N(s.FeedId)}\">")
                .Append($"<input type=\"hidden\" name=\"mute\" value=\"{(s.Muted ? "0" : "1")}\"><input type=\"hidden\" name=\"back\" value=\"{E(back)}\">")
                .Append($"<button type=\"submit\" class=\"quiet\" aria-label=\"{(s.Muted ? "Unmute" : "Mute")} {E(s.Title)}\">{(s.Muted ? "Unmute" : "Mute")}</button></form></td></tr>");
        }

        h.Append("</tbody></table></div>");
        return Layout(p, "Sources", "/ui/sources", h.ToString());
    }

    public static string Weekly(PageContext p, WeeklyRow? row, IReadOnlyList<string> weeks)
    {
        var h = new StringBuilder("<h1>Weekly review</h1>");
        if (row is null)
        {
            h.Append("<p class=\"empty\">No review yet. One is built on Sunday at 18:30.</p>");
            return Layout(p, "Weekly review", "/ui/weekly", h.ToString());
        }

        var r = row.Report;
        h.Append($"<p class=\"meta\">Week ending {E(row.WeekOf)} · {E(Local(p, r.Since))} to {E(Local(p, r.Until))}{(row.SentAt is { } sent ? " · sent " + E(Local(p, sent)) : " · not sent")}</p>");
        h.Append("<dl class=\"stats\">")
            .Append($"<div><dt>Items published</dt><dd>{N(r.Items)}</dd></div><div><dt>Shown in digests</dt><dd>{N(r.Shown)}</dd></div>")
            .Append($"<div><dt>Rated</dt><dd>👍 {N(r.Up)} · 👎 {N(r.Down)} · 💡 {N(r.Ideas)}</dd></div></dl>");

        h.Append("<h2>Top 👍</h2>");
        if (r.TopLiked.Count == 0)
        {
            h.Append("<p class=\"empty\">Nothing liked this week.</p>");
        }
        else
        {
            h.Append("<ol>");
            foreach (var i in r.TopLiked)
            {
                h.Append($"<li>{External(i.Url, i.Title)} <span class=\"muted\">{E(i.Feed)}</span> · <a href=\"/ui/item/{N(i.Id)}\">Open</a></li>");
            }

            h.Append("</ol>");
        }

        h.Append("<h2>Rated by project</h2>");
        if (r.Projects.Count == 0)
        {
            h.Append("<p class=\"empty\">No ratings.</p>");
        }
        else
        {
            h.Append("<div class=\"scroll\"><table><thead><tr><th>Project</th><th class=\"num\">👍</th><th class=\"num\">👎</th></tr></thead><tbody>");
            foreach (var t in r.Projects)
            {
                h.Append($"<tr><td><span class=\"badge\">{E(t.Project)}</span></td><td class=\"num\">{N(t.Up)}</td><td class=\"num\">{N(t.Down)}</td></tr>");
            }

            h.Append("</tbody></table></div>");
        }

        h.Append("<h2>Ideas filed</h2>");
        if (r.IdeasFiled.Count == 0)
        {
            h.Append("<p class=\"empty\">None this week.</p>");
        }
        else
        {
            h.Append("<ul>");
            foreach (var i in r.IdeasFiled)
            {
                h.Append($"<li>{E(i.Title)} <span class=\"badge\">{E(i.PlaneProject)}</span> · <a href=\"/ui/item/{N(i.ItemId)}\">Source item</a></li>");
            }

            h.Append("</ul>");
        }

        h.Append("<h2>Feeds that earned 👍</h2>");
        h.Append(r.TopFeeds.Count == 0
            ? "<p class=\"empty\">None.</p>"
            : "<ul>" + string.Concat(r.TopFeeds.Select(f => $"<li>{E(f.Title)} <span class=\"muted\">{N(f.Count)}</span></li>")) + "</ul>");
        h.Append("<h2>Mute candidates</h2><p class=\"meta\">Posts this week, none shown in a digest and none liked. Mute them on the <a href=\"/ui/sources?sort=perweek&amp;dir=desc\">Sources page</a>.</p>");
        h.Append(r.MuteCandidates.Count == 0
            ? "<p class=\"empty\">None.</p>"
            : "<ul>" + string.Concat(r.MuteCandidates.Select(f => $"<li>{E(f.Title)} <span class=\"muted\">{N(f.Count)} posts</span></li>")) + "</ul>");

        if (weeks.Count > 1)
        {
            h.Append("<h2>History</h2><ul>");
            foreach (var w in weeks)
            {
                h.Append(w == row.WeekOf ? $"<li>{E(w)} (this one)</li>" : $"<li><a href=\"/ui/weekly/{E(w)}\">{E(w)}</a></li>");
            }

            h.Append("</ul>");
        }

        return Layout(p, "Weekly review", "/ui/weekly", h.ToString());
    }

    public static string Releases(PageContext p, IReadOnlyList<WatchedProduct> watched, IReadOnlyList<ReleaseRow> releases)
    {
        var h = new StringBuilder("<h1>Releases</h1><p class=\"meta\">Products you run (from PINS.md), matched to release feeds. Nothing is applied for you.</p>");
        if (watched.Count == 0)
        {
            h.Append("<p class=\"empty\">Nothing is watched. Set FeedEater:Watch:Source or ship config/watch.json.</p>");
        }
        else
        {
            h.Append("<div class=\"scroll\"><table><thead><tr><th>Product</th><th>Services</th><th>Running</th><th>Latest seen</th><th>Status</th></tr></thead><tbody>");
            foreach (var w in watched)
            {
                var latest = releases.Where(r => r.Repo == w.Repo).Select(r => (Row: r, V: Versions.Parse(r.Version))).Where(x => x.V is not null)
                    .OrderByDescending(x => x.V!, Comparer<int[]>.Create(Versions.Compare)).Select(x => x.Row).FirstOrDefault();
                var status = w.Running is null ? ("unknown", "warn")
                    : latest is not null && Versions.Parse(latest.Version) is { } lv && Versions.Compare(lv, w.Running) > 0 ? ("update available", "bad")
                    : ("up to date", "good");
                h.Append($"<tr><td>{E(w.Repo)}</td><td>{E(string.Join(", ", w.Services))}</td><td>{E(w.RunningText ?? "unknown")}</td>")
                    .Append($"<td>{(latest is null ? "–" : External(latest.Url, latest.Version))}</td><td><span class=\"badge {status.Item2}\">{E(status.Item1)}</span></td></tr>");
            }

            h.Append("</tbody></table></div>");
        }

        h.Append("<h2>Recent releases</h2>");
        var news = releases.Where(r => r.Newer).Take(30).ToList();
        if (news.Count == 0)
        {
            h.Append("<p class=\"empty\">No newer releases seen yet.</p>");
        }
        else
        {
            h.Append("<ul class=\"releases\">");
            foreach (var r in news)
            {
                var how = r.AnnouncedAt is not null ? "sent on Telegram" : r.DigestDate is not null ? "in the digest of " + r.DigestDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "waiting for the next digest";
                h.Append($"<li><strong>{External(r.Url, r.Repo + " " + r.Version)}</strong> <span class=\"badge {(r.Breaking == "yes" ? "bad" : r.Breaking == "no" ? "good" : "warn")}\">breaking: {E(r.Breaking ?? "unknown")}</span>")
                    .Append(r.Urgent ? " <span class=\"badge bad\">security or breaking</span>" : "")
                    .Append($"<p class=\"meta\">{E(Local(p, r.DetectedAt))} · {E(how)}</p>");
                if (r.Summary is not null)
                {
                    h.Append($"<p>{E(r.Summary)}</p>");
                }

                if (r.Evidence is not null)
                {
                    h.Append($"<p class=\"why\">&ldquo;{E(r.Evidence)}&rdquo;</p>");
                }

                h.Append("</li>");
            }

            h.Append("</ul>");
        }

        return Layout(p, "Releases", "/ui/releases", h.ToString());
    }

    public static string Ideas(PageContext p, IReadOnlyList<Idea> ideas)
    {
        var h = new StringBuilder("<h1>Ideas</h1>");
        if (ideas.Count == 0)
        {
            h.Append("<p class=\"empty\">Nothing filed yet. Press 💡 on a digest item that has a suggestion.</p>");
        }
        else
        {
            h.Append("<div class=\"scroll\"><table><thead><tr><th>Filed</th><th>Plane project</th><th>Idea</th><th>Source item</th></tr></thead><tbody>");
            foreach (var i in ideas)
            {
                h.Append($"<tr><td>{E(Local(p, i.At))}</td><td><span class=\"badge\">{E(i.PlaneProject)}</span></td><td>{E(i.Title)}</td>")
                    .Append($"<td><a href=\"/ui/item/{N(i.ItemId)}\">Item {N(i.ItemId)}</a></td></tr>");
            }

            h.Append("</tbody></table></div>");
        }

        return Layout(p, "Ideas", "/ui/ideas", h.ToString());
    }

    public static string Usage(PageContext p, decimal month, decimal budget, IReadOnlyList<DaySpend> days, IReadOnlyList<PurposeSpend> purposes, decimal total)
    {
        var h = new StringBuilder("<h1>Usage</h1>");
        h.Append($"<p class=\"big\">{Money(month)} <span class=\"muted\">this month</span></p>");
        if (budget > 0)
        {
            h.Append($"<p><meter min=\"0\" max=\"{Number(budget)}\" value=\"{Number(Math.Min(month, budget))}\" aria-label=\"Month spend against budget\"></meter> {Money(month)} of {Money(budget)} ({Percent((double)(month / budget))})</p>");
        }

        h.Append($"<h2>By day, last 30 days · {Money(total)}</h2>");
        if (days.Count == 0)
        {
            h.Append("<p class=\"empty\">No spend recorded.</p>");
        }
        else
        {
            var max = days.Max(d => d.Cost);
            h.Append("<div class=\"scroll\"><table><thead><tr><th>Day</th><th class=\"num\">Spend</th><th class=\"num\">Tokens</th><th></th></tr></thead><tbody>");
            foreach (var d in days)
            {
                h.Append($"<tr><td>{d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}</td><td class=\"num\">{Money(d.Cost)}</td><td class=\"num\">{d.Tokens.ToString("N0", CultureInfo.InvariantCulture)}</td>")
                    .Append($"<td><meter min=\"0\" max=\"{Number(max)}\" value=\"{Number(d.Cost)}\" aria-label=\"Spend on {d.Day.ToString("d MMM", CultureInfo.InvariantCulture)}\"></meter></td></tr>");
            }

            h.Append("</tbody></table></div>");
        }

        h.Append("<h2>By purpose and model</h2>");
        if (purposes.Count == 0)
        {
            h.Append("<p class=\"empty\">No calls recorded.</p>");
        }
        else
        {
            h.Append("<div class=\"scroll\"><table><thead><tr><th>Purpose</th><th>Model</th><th class=\"num\">Calls</th><th class=\"num\">Spend</th></tr></thead><tbody>");
            foreach (var s in purposes)
            {
                h.Append($"<tr><td>{E(s.Purpose)}</td><td>{E(s.Model)}</td><td class=\"num\">{N(s.Calls)}</td><td class=\"num\">{Money(s.Cost)}</td></tr>");
            }

            h.Append("</tbody></table></div>");
        }

        return Layout(p, "Usage", "/ui/usage", h.ToString());
    }

    private static void Stats(StringBuilder h, Figures f)
    {
        h.Append("<dl class=\"stats\">")
            .Append($"<div><dt>Spend this month</dt><dd>{Money(f.MonthSpend)}{(f.Budget > 0 ? $" <span class=\"muted\">of {Money(f.Budget)}</span>" : "")}</dd></div>")
            .Append($"<div><dt>7-day 👍 rate</dt><dd>{Percent(f.WeekUpRate)}</dd></div>")
            .Append($"<div><dt>Yesterday</dt><dd>👍 {N(f.Yesterday.Up)} · 👎 {N(f.Yesterday.Down)}</dd></div></dl>");
    }

    private static void RunControls(StringBuilder h, PageContext p, RunPanel run)
    {
        h.Append("<section class=\"panel\" aria-labelledby=\"run-h\"><h2 id=\"run-h\">Run the digest</h2>");
        if (!run.Enabled)
        {
            h.Append("<p class=\"muted\">The digest job is off: Telegram is not configured.</p></section>");
            return;
        }

        if (run.Pending is not null)
        {
            h.Append($"<p role=\"status\"><span class=\"badge warn\">queued</span> {E(run.Pending)} run requested; it starts within a minute.</p>");
        }
        else if (run.Last is { } last)
        {
            h.Append($"<p role=\"status\">Last forced run, {E(Local(p, last.At))}: {E(last.Text)}</p>");
        }

        h.Append($"<form method=\"post\" action=\"/ui/digest/run\" class=\"inline\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(p.Csrf)}\"><input type=\"hidden\" name=\"mode\" value=\"run\"><button type=\"submit\" class=\"primary\">Run digest now</button></form>");
        if (run.Sent)
        {
            h.Append(
                $"""
                <details class="confirm"><summary>Send today's digest again</summary>
                  <form method="post" action="/ui/digest/run" class="stack">
                    <input type="hidden" name="_csrf" value="{E(p.Csrf)}"><input type="hidden" name="mode" value="resend">
                    <p>Today's digest was already sent. This sends the same messages to Telegram a second time.</p>
                    <button type="submit" class="danger">Yes, send it again</button>
                  </form></details>
                """);
        }

        h.Append("</section>");
    }

    private static void Card(StringBuilder h, PageContext p, ItemView v, string back, bool full)
    {
        h.Append($"<article class=\"card\" id=\"item-{N(v.Id)}\">");
        var title = External(v.Url, v.Title);
        h.Append(full ? $"<h1>{title}</h1>" : $"<h2>{title}</h2>");
        h.Append("<p class=\"meta\">").Append(Meta(p, v.Feed, v.Category, v.Project, v.Kind, v.PublishedAt));
        if (!full)
        {
            h.Append($" · <a href=\"/ui/item/{N(v.Id)}\">Open</a>");
        }

        h.Append("</p>");
        if (v.Summary is not null)
        {
            h.Append($"<p>{E(v.Summary)}</p>");
        }
        else if (!full && v.Content.Length > 0)
        {
            h.Append($"<p class=\"excerpt\">{E(Excerpt(v.Content))}</p>");
        }

        if (v.AlsoIn.Count > 0)
        {
            h.Append("<p class=\"also\">Also in: ").Append(string.Join(", ", v.AlsoIn.Take(6).Select(m => External(m.Url, m.Feed.Length > 0 ? m.Feed : m.Title))));
            h.Append(v.AlsoIn.Count > 6 ? $" +{N(v.AlsoIn.Count - 6)}" : "").Append("</p>");
        }
        else if (v.Also > 0)
        {
            h.Append($"<p class=\"also\">{N(v.Also)} similar {(v.Also == 1 ? "item" : "items")}</p>");
        }

        if (v.Why is not null)
        {
            h.Append($"<p class=\"why\">{E(v.Why)}</p>");
        }

        if (v.Suggestion is not null)
        {
            h.Append($"<p class=\"suggestion\"><strong>Suggestion</strong> {E(v.Suggestion)}</p>");
        }

        h.Append($"<form method=\"post\" action=\"/ui/vote\" class=\"actions\"><input type=\"hidden\" name=\"_csrf\" value=\"{E(p.Csrf)}\">")
            .Append($"<input type=\"hidden\" name=\"item\" value=\"{N(v.Id)}\"><input type=\"hidden\" name=\"back\" value=\"{E(back)}#item-{N(v.Id)}\">")
            .Append($"<button type=\"submit\" name=\"v\" value=\"up\" aria-pressed=\"{(v.Vote == 1 ? "true" : "false")}\">👍 Like</button>")
            .Append($"<button type=\"submit\" name=\"v\" value=\"down\" aria-pressed=\"{(v.Vote == -1 ? "true" : "false")}\">👎 Dislike</button>");
        if (v.FiledIn is not null)
        {
            h.Append($"<span class=\"badge good\">Filed in {E(v.FiledIn)}</span>");
        }
        else if (v.Suggestion is not null)
        {
            h.Append("<button type=\"submit\" name=\"v\" value=\"idea\">💡 File to Plane</button>");
        }

        h.Append(v.Saved
            ? "<span class=\"badge good\">📌 Saved</span>"
            : "<button type=\"submit\" name=\"v\" value=\"save\">📌 Save</button>");
        if (v.Vote is not null)
        {
            h.Append("<button type=\"submit\" name=\"v\" value=\"clear\" class=\"quiet\">Clear vote</button>");
        }

        h.Append("</form></article>");
    }

    private static string Excerpt(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 280 ? flat : flat[..279].TrimEnd() + "…";
    }

    private static string Meta(PageContext p, string feed, string? category, string? project, string? kind, DateTime published)
    {
        var parts = new List<string> { $"<span>{E(feed)}{(string.IsNullOrEmpty(category) ? "" : $" <span class=\"muted\">in {E(category)}</span>")}</span>" };
        if (!string.IsNullOrEmpty(project))
        {
            parts.Add($"<span class=\"badge\">{E(project)}</span>");
        }

        if (!string.IsNullOrEmpty(kind))
        {
            parts.Add($"<span class=\"badge kind-{(kind is "improve" or "new" or "fyi" ? kind : "other")}\">{E(kind)}</span>");
        }

        parts.Add($"<time datetime=\"{published.ToString("O", CultureInfo.InvariantCulture)}\">{E(Local(p, published))}</time>");
        return string.Join(" · ", parts);
    }

    private static string Layout(PageContext p, string title, string? active, string body)
    {
        var nav = new StringBuilder("<nav aria-label=\"Main\">");
        foreach (var (path, label) in Nav)
        {
            nav.Append($"<a href=\"{path}\"{(path == active ? " aria-current=\"page\"" : "")}>{label}</a>");
        }

        nav.Append("</nav>");
        var notice = p.Notice is not null && Notices.TryGetValue(p.Notice, out var text)
            ? $"<p class=\"notice good\" role=\"status\">{E(text)}</p>"
            : "";
        return Shell(title,
            $"""
            <header class="top"><span class="brand">feed-eater</span>{nav}
              <form method="post" action="/ui/logout"><input type="hidden" name="_csrf" value="{E(p.Csrf)}"><button type="submit" class="link">Sign out</button></form>
            </header>
            <main id="main">{notice}{body}</main>
            """);
    }

    private static string Shell(string title, string body) =>
        $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <meta name="color-scheme" content="dark light"><meta name="referrer" content="same-origin">
        <title>{E(title)} · feed-eater</title><link rel="stylesheet" href="/ui/app.css"></head>
        <body><a class="skip" href="#main">Skip to content</a>{body}</body></html>
        """;

    private static string SourcesPath(string sort, bool desc, bool flagged) =>
        $"/ui/sources?sort={sort}&dir={(desc ? "desc" : "asc")}{(flagged ? "&flag=1" : "")}";

    private static string Option(string value, string label, string? selected) =>
        $"<option value=\"{E(value)}\"{((selected ?? "") == value ? " selected" : "")}>{E(label)}</option>";

    private static string StatusClass(string status) => status switch { "sent" => "good", "failed" => "bad", _ => "warn" };

    private static string Local(PageContext p, DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), p.Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Local(PageContext p, DateTimeOffset at) => Local(p, at.UtcDateTime);

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Number(decimal d) => d.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Money(decimal d) => "$" + (d >= 0.01m || d == 0 ? d.ToString("0.00", CultureInfo.InvariantCulture) : d.ToString("0.0000", CultureInfo.InvariantCulture));

    private static string Percent(double? rate) => rate is { } r ? Math.Round(r * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%" : "–";
}

public sealed record SearchQuery(string Text, string? Project, string? Kind, string? From, string? To);
