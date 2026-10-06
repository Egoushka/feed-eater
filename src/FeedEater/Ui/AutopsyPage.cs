using System.Globalization;
using System.Text;
using FeedEater.Hype;
using FeedEater.Storage;
using static FeedEater.Ui.Html;

namespace FeedEater.Ui;

public static partial class UiPages
{
    private const int MaxFeedRows = 30;

    /// <summary><paramref name="on"/> is whether the autopsy jobs can run; it only decides what an empty page says.</summary>
    public static string Autopsy(PageContext p, AutopsyRow? row, IReadOnlyList<string> months, bool on)
    {
        var h = new StringBuilder("<h1>Hype autopsy</h1>");
        if (row is null)
        {
            h.Append(on
                ? $"<p class=\"empty\">No autopsy yet. One is built on the 1st of a month at 09:00, for 👍 items whose repo snapshot is at least {N(AutopsyRules.MinAgeDays)} days old.</p>"
                : "<p class=\"empty\">The autopsy is off: it needs GitHub:User and Telegram:Token to be set.</p>");
            return Layout(p, "Hype autopsy", "/ui/autopsy", h.ToString());
        }

        var r = row.Report;
        h.Append($"<p class=\"meta\">Month of {E(AutopsyFormatter.MonthName(row.Month))} · built {E(Local(p, r.At))}{(row.SentAt is { } sent ? " · sent " + E(Local(p, sent)) : " · not sent")}</p>");
        h.Append($"<p class=\"meta\">{E(AutopsyRules.Legend)}</p>");
        h.Append("<dl class=\"stats\">")
            .Append($"<div><dt>Scored</dt><dd>{N(r.Items.Count)}</dd></div><div><dt>Lasted (grew or alive)</dt><dd>{E(AutopsyFormatter.Pct(r.Total.Lasted, r.Total.Total))}</dd></div>")
            .Append($"<div><dt>No GitHub repo</dt><dd>{N(r.NoRepo)}</dd></div><div><dt>Gone when first seen</dt><dd>{N(r.NoBaseline)}</dd></div>")
            .Append($"<div><dt>Not checked (tried next month)</dt><dd>{N(r.Unchecked)}</dd></div></dl>");

        h.Append("<h2>By the model's relevance</h2>");
        AutopsyGroups(h, "Relevance", r.ByRelevance, null);
        h.Append("<h2>By feed</h2>");
        AutopsyGroups(h, "Feed", r.ByFeed, MaxFeedRows);

        h.Append("<h2>Items</h2><div class=\"scroll\"><table><thead><tr><th>Verdict</th><th>Item</th><th>Repo</th><th class=\"num\">Stars</th><th>Pushed recently</th><th>New release</th><th>Idea</th><th>Saved</th><th class=\"num\">Related later</th><th>Relevance</th></tr></thead><tbody>");
        foreach (var i in r.Items)
        {
            var stars = i.StarsNow is { } now ? $"{N(i.StarsThen)} to {N(now)}" : $"{N(i.StarsThen)} to none";
            var growth = i.StarsGrowthPercent is { } g ? $" <span class=\"muted\">{(g >= 0 ? "+" : "")}{g.ToString("0", CultureInfo.InvariantCulture)}%</span>" : "";
            h.Append($"<tr><td><span class=\"badge {VerdictClass(i.Verdict)}\">{E(i.Verdict.ToString().ToLowerInvariant())}</span></td>")
                .Append($"<td>{External(i.Url, i.Title)} <span class=\"muted\">{E(i.Feed)}</span> · <a href=\"/ui/item/{N(i.ItemId)}\">Open</a></td>")
                .Append($"<td>{External("https://github.com/" + i.Repo, i.Repo)}</td><td class=\"num\">{stars}{growth}</td>")
                .Append($"<td>{(i.PushedRecently ? "yes" : "no")}</td><td>{(i.NewRelease ? "yes" : "no")}</td><td>{(i.Idea == IdeaOutcome.None ? "no" : E(i.Idea.ToString().ToLowerInvariant()))}</td>")
                .Append($"<td>{(i.Saved ? "yes" : "no")}</td><td class=\"num\">{N(i.RelatedLater)}</td><td>{E(i.Relevance?.ToString(CultureInfo.InvariantCulture) ?? AutopsyReport.NoRelevance)}</td></tr>");
        }

        h.Append("</tbody></table></div>");

        if (months.Count > 1)
        {
            h.Append("<h2>History</h2><ul>");
            foreach (var m in months)
            {
                h.Append(m == row.Month ? $"<li>{E(m)} (this one)</li>" : $"<li><a href=\"/ui/autopsy/{E(m)}\">{E(m)}</a></li>");
            }

            h.Append("</ul>");
        }

        return Layout(p, "Hype autopsy", "/ui/autopsy", h.ToString());
    }

    private static void AutopsyGroups(StringBuilder h, string first, IReadOnlyList<AutopsyGroup> groups, int? limit)
    {
        if (groups.Count == 0)
        {
            h.Append("<p class=\"empty\">Nothing scored.</p>");
            return;
        }

        h.Append($"<div class=\"scroll\"><table><thead><tr><th>{E(first)}</th><th class=\"num\">Items</th><th class=\"num\">Grew</th><th class=\"num\">Alive</th><th class=\"num\">Quiet</th><th class=\"num\">Gone</th><th class=\"num\">Lasted</th></tr></thead><tbody>");
        foreach (var g in groups.Take(limit ?? groups.Count))
        {
            var c = g.Counts;
            h.Append($"<tr><td>{E(g.Label)}</td><td class=\"num\">{N(c.Total)}</td>")
                .Append(Share(c.Grew, c.Total)).Append(Share(c.Alive, c.Total)).Append(Share(c.Quiet, c.Total)).Append(Share(c.Gone, c.Total)).Append(Share(c.Lasted, c.Total))
                .Append("</tr>");
        }

        h.Append("</tbody></table></div>");
    }

    private static string Share(int part, int total) => $"<td class=\"num\">{N(part)} <span class=\"muted\">{E(AutopsyFormatter.Pct(part, total))}</span></td>";

    private static string VerdictClass(RepoVerdict v) => v switch { RepoVerdict.Grew => "good", RepoVerdict.Quiet => "warn", RepoVerdict.Gone => "bad", _ => "" };
}
