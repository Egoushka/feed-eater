using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Swipe;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

/// <summary><c>/ui/swipe</c>: how swipe mode is going, per week and per category. Dwell is shown on purpose and ranks nothing.</summary>
public sealed class SwipeReportHandler(SwipeStore store, CategoryStore categories, UiSession session, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    private const int Weeks = 12;
    private const int CategoryDays = 30;

    public async Task<IResult> GetAsync(HttpContext ctx, CancellationToken ct)
    {
        var settings = options.Value;
        var page = new PageContext(session.AntiForgery(ctx.Request.Cookies[UiSession.CookieName] ?? ""), settings.Zone, null,
            ButtonStyle.From(settings), File.Exists(settings.ProfilePath) ? null : settings.ProfilePath);
        var weeks = await store.WeeksAsync(settings.TimeZone, Weeks, ct);
        var counts = await store.CategoryCardsAsync(time.GetUtcNow().AddDays(-CategoryDays), ct);
        var wanted = (await categories.AllAsync(ct)).Where(c => c.Share is > 0).ToDictionary(c => c.Key, c => (double)c.Share!.Value, StringComparer.Ordinal);
        return UiHandlers.Html(UiPages.Swipe(page, settings.Swipe.Enabled, weeks, counts, wanted));
    }
}

public static partial class UiPages
{
    public static string Swipe(PageContext p, bool enabled, IReadOnlyList<WeekRow> weeks, IReadOnlyList<CategoryCount> categories, IReadOnlyDictionary<string, double> wanted)
    {
        var h = new StringBuilder("<h1>Swipe</h1>");
        if (!enabled)
        {
            h.Append("<p class=\"notice warn\">Swipe mode is off. Set <code>FeedEater__Swipe__Enabled=true</code> and open <code>/swipe</code> on the phone.</p>");
        }

        if (weeks.Count == 0)
        {
            h.Append("<p class=\"empty\">No sessions yet.</p>");
            return Layout(p, "Swipe", "/ui/swipe", h.ToString());
        }

        h.Append("<div class=\"scroll\"><table><thead><tr><th>Week of</th><th class=\"num\">Sessions</th><th class=\"num\">Cards</th><th class=\"num\">Minutes</th>")
            .Append("<th class=\"num\">📌</th><th class=\"num\">📤</th><th class=\"num\">💡</th><th class=\"num\">👍</th><th class=\"num\">👎</th><th class=\"num\">↗</th>")
            .Append("<th class=\"num\">Good acts per 100 cards</th><th class=\"num\">Dwell per card (s)</th><th class=\"num\">Lighter / same / drained</th></tr></thead><tbody>");
        foreach (var w in weeks)
        {
            var good = w.Saves + w.Shares + w.Ideas;
            var rate = w.Cards == 0 ? "–" : (good * 100.0 / w.Cards).ToString("0.#", CultureInfo.InvariantCulture);
            var dwell = w.AvgDwellS is { } d ? d.ToString("0.#", CultureInfo.InvariantCulture) : "–";
            h.Append($"<tr><td>{E(w.Week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}</td><td class=\"num\">{N(w.Sessions)}</td><td class=\"num\">{N(w.Cards)}</td>")
                .Append($"<td class=\"num\">{w.Minutes.ToString("0", CultureInfo.InvariantCulture)}</td><td class=\"num\">{N(w.Saves)}</td><td class=\"num\">{N(w.Shares)}</td>")
                .Append($"<td class=\"num\">{N(w.Ideas)}</td><td class=\"num\">{N(w.Likes)}</td><td class=\"num\">{N(w.Dislikes)}</td><td class=\"num\">{N(w.Opens)}</td>")
                .Append($"<td class=\"num\">{rate}</td><td class=\"num\">{dwell}</td><td class=\"num\">{N(w.Lighter)} / {N(w.Same)} / {N(w.Drained)}</td></tr>");
        }

        h.Append("</tbody></table></div>");
        h.Append("<p class=\"meta\">Good acts are saves, shares and ideas. Dwell is shown to compare it with the acts; the ranking never reads it. The last column is the one-tap answer to \"how did this leave me\", per session.</p>");

        h.Append("<h2>Cards by category, last 30 days</h2>");
        var total = categories.Sum(c => c.Cards);
        h.Append("<div class=\"scroll\"><table><thead><tr><th>Category</th><th class=\"num\">Cards</th><th class=\"num\">Share of cards</th><th class=\"num\">Wanted share</th></tr></thead><tbody>");
        foreach (var c in categories)
        {
            var share = total == 0 ? "–" : Percent(c.Cards / (double)total);
            var want = wanted.TryGetValue(c.Category, out var s) ? Percent(s) : "–";
            h.Append($"<tr><td><span class=\"badge\">{E(c.Category)}</span></td><td class=\"num\">{N(c.Cards)}</td><td class=\"num\">{share}</td><td class=\"num\">{want}</td></tr>");
        }

        h.Append("</tbody></table></div>");
        return Layout(p, "Swipe", "/ui/swipe", h.ToString());
    }
}
