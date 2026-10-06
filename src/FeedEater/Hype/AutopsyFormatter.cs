using System.Globalization;
using System.Net;
using System.Text;
using FeedEater.Digest;
using FeedEater.Telegram;

namespace FeedEater.Hype;

/// <summary>The monthly autopsy as one Telegram HTML message. Lists are capped so the visible text stays far under 4,096 characters.</summary>
public static class AutopsyFormatter
{
    private const int MaxItems = 8;
    private const int MaxFeeds = 5;

    public static OutMessage Message(string month, AutopsyReport r)
    {
        var h = new StringBuilder($"<b>Hype autopsy · {E(MonthName(month))}</b>\n");
        h.Append(CultureInfo.InvariantCulture, $"{r.Items.Count} 👍 items, a repo checked at least {AutopsyRules.MinAgeDays} days after: {Counts(r.Total)}");

        var unscored = new List<string>();
        Note(unscored, r.NoRepo, "link no GitHub repo");
        Note(unscored, r.NoBaseline, "were already gone when first looked up");
        Note(unscored, r.Unchecked, "could not be checked now (GitHub refused; tried again next month)");
        if (unscored.Count > 0)
        {
            h.Append("\nNot scored: ").Append(string.Join("; ", unscored));
        }

        if (r.ByRelevance.Count > 0)
        {
            h.Append("\n\n<b>By the model's relevance</b>");
            foreach (var g in r.ByRelevance)
            {
                h.Append(CultureInfo.InvariantCulture, $"\n{E(g.Label)}: {g.Counts.Total} items, {Pct(g.Counts.Lasted, g.Counts.Total)} lasted ({Counts(g.Counts)})");
            }
        }

        if (r.ByFeed.Count > 0)
        {
            h.Append("\n\n<b>Feeds, by items that lasted</b>\n").Append(string.Join(" · ", r.ByFeed.Take(MaxFeeds)
                .Select(f => string.Create(CultureInfo.InvariantCulture, $"{E(DigestFormatter.Clip(f.Label, 40))} {f.Counts.Lasted}/{f.Counts.Total}"))));
        }

        if (r.Items.Count > 0)
        {
            h.Append("\n\n<b>Items</b>");
            foreach (var i in r.Items.Take(MaxItems))
            {
                var title = E(DigestFormatter.Clip(i.Title, 90));
                var link = IsLinkable(i.Url) ? $"<a href=\"{E(i.Url)}\">{title}</a>" : title;
                h.Append(CultureInfo.InvariantCulture, $"\n{i.Verdict.ToString().ToLowerInvariant()} · {link} <i>{E(DigestFormatter.Clip(i.Repo, 50))}</i>");
            }

            if (r.Items.Count > MaxItems)
            {
                h.Append(CultureInfo.InvariantCulture, $"\n+{r.Items.Count - MaxItems} more");
            }
        }

        h.Append("\n\n<i>").Append(E(AutopsyRules.Legend)).Append("</i>");
        return new OutMessage(h.ToString());
    }

    /// <summary>"grew 40% · alive 20% · quiet 40% · gone 0%"</summary>
    internal static string Counts(VerdictCounts c) => string.Create(CultureInfo.InvariantCulture,
        $"grew {Pct(c.Grew, c.Total)} · alive {Pct(c.Alive, c.Total)} · quiet {Pct(c.Quiet, c.Total)} · gone {Pct(c.Gone, c.Total)}");

    internal static string Pct(int part, int total) =>
        total == 0 ? "0%" : (100.0 * part / total).ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string MonthName(string month) =>
        DateOnly.TryParseExact(month, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : month;

    private static void Note(List<string> notes, int count, string what)
    {
        if (count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {what}"));
        }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static bool IsLinkable(string url) =>
        url.Length <= 1000 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
