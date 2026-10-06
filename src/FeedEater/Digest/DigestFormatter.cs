using System.Globalization;
using System.Net;
using System.Text;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Digest;

public sealed record DigestItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public string? Project { get; init; }
    public string Kind { get; init; } = "fyi";
    public string Summary { get; init; } = "";
    public string Why { get; init; } = "";
    public string? Suggestion { get; init; }
    public IReadOnlyList<ClusterMember> AlsoIn { get; init; } = [];
}

/// <summary>
/// <c>MonthSpend</c> is the known spend since the 1st of the current month and <c>UnpricedCalls</c> the calls in it whose cost is unknown;
/// <c>WeekUpRate</c> is the 👍 share of the last 7 days' votes, null when there were none.
/// </summary>
public sealed record DigestHeader(
    DateOnly Date, int Shown, int Candidates, IReadOnlyList<(string Key, int Count)> ByProject,
    int VotesUp, int VotesDown, decimal MonthSpend, double? WeekUpRate, IReadOnlyList<string> Notes, IReadOnlyList<string>? Releases = null, int UnpricedCalls = 0);

/// <summary>Which optional buttons a surface offers: 📌 needs Karakeep, and 💡 says where the idea goes.</summary>
public sealed record ButtonStyle(bool CanSave, bool ToPlane)
{
    public static readonly ButtonStyle Default = new(true, true);

    public static ButtonStyle From(FeedEaterOptions o) => new(o.Karakeep.Enabled, o.IdeaSink == IdeasOptions.PlaneSink);
}

/// <summary>
/// Telegram HTML. Every field is escaped and clipped so the visible text stays under 4,096 characters
/// (Telegram counts text after entity parsing, so escapes and the href do not count).
/// </summary>
public static class DigestFormatter
{
    public const int MaxLength = 4096;
    private const int MaxLinkedUrl = 1000;

    public static OutMessage Header(DigestHeader h)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<b>Feed digest · {h.Date.ToString("d MMM", CultureInfo.InvariantCulture)}</b>\n");
        sb.Append(CultureInfo.InvariantCulture, $"{h.Shown} of {h.Candidates} new items");
        if (h.ByProject.Count > 0)
        {
            sb.Append('\n').Append(string.Join(" · ", h.ByProject.Select(p => string.Create(CultureInfo.InvariantCulture, $"{E(p.Key)} {p.Count}"))));
        }

        sb.Append(CultureInfo.InvariantCulture, $"\nYesterday 👍 {h.VotesUp} · 👎 {h.VotesDown} · spend this month {Spend(h.MonthSpend, h.UnpricedCalls)}");
        if (h.WeekUpRate is { } rate)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n7-day 👍 rate {Math.Round(rate * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%");
        }

        foreach (var note in h.Notes)
        {
            sb.Append("\n⚠️ ").Append(E(Clip(note, 300)));
        }

        if (h.Releases is { Count: > 0 })
        {
            sb.Append("\n\n<b>Updates for what you run</b>");
            foreach (var line in h.Releases.Take(8))
            {
                sb.Append("\n• ").Append(E(Clip(line, 160)));
            }

            if (h.Releases.Count > 8)
            {
                sb.Append(CultureInfo.InvariantCulture, $"\n+{h.Releases.Count - 8} more");
            }
        }

        return new OutMessage(sb.ToString());
    }

    public static OutMessage Item(DigestItem i, short? vote, string? filedIn, ButtonStyle? style = null)
    {
        var title = E(Clip(i.Title, 300));
        var link = IsLinkable(i.Url) ? $"<a href=\"{E(i.Url)}\">{title}</a>" : title;
        var meta = string.Join(" · ", new[] { Clip(i.Feed, 80), Clip(i.Project ?? "", 40), Clip(i.Kind, 40) }.Where(s => s.Length > 0).Select(E));
        var html = new StringBuilder()
            .Append(i.Suggestion is null ? "📰" : "💡").Append(" <b>").Append(link).Append("</b>\n")
            .Append("<i>").Append(meta).Append("</i>\n\n")
            .Append(E(Clip(i.Summary, 1200))).Append('\n')
            .Append(E(Clip(i.Why, 600)));
        if (i.Suggestion is not null)
        {
            html.Append("\n\n💡 ").Append(E(Clip(i.Suggestion, 800)));
        }

        if (i.AlsoIn.Count > 0)
        {
            var links = i.AlsoIn.Take(3).Select(m => IsLinkable(m.Url) ? $"<a href=\"{E(m.Url)}\">{E(Clip(m.Feed, 40))}</a>" : E(Clip(m.Feed, 40)));
            html.Append("\n\nAlso in: ").Append(string.Join(", ", links));
            if (i.AlsoIn.Count > 3)
            {
                html.Append(CultureInfo.InvariantCulture, $" +{i.AlsoIn.Count - 3}");
            }
        }

        return new OutMessage(html.ToString(), Buttons(i.Id, i.Suggestion is not null, vote, filedIn, style: style));
    }

    /// <summary>One search hit as its own message, with the same buttons as a digest item.</summary>
    public static OutMessage Result(SearchHit h, string publishedLocal, ButtonStyle? style = null)
    {
        var title = E(Clip(h.Title, 300));
        var link = IsLinkable(h.Url) ? $"<a href=\"{E(h.Url)}\">{title}</a>" : title;
        var meta = string.Join(" · ", new[] { Clip(h.Feed, 80), publishedLocal, Clip(h.Project ?? "", 40) }.Where(s => s.Length > 0).Select(E));
        var html = new StringBuilder($"<b>{link}</b>\n<i>{meta}</i>");
        if (!string.IsNullOrWhiteSpace(h.Summary))
        {
            html.Append("\n\n").Append(E(Clip(h.Summary.Trim(), 240)));
        }

        return new OutMessage(html.ToString(), Buttons(h.Id, h.HasSuggestion, (short?)h.Vote, h.FiledIn, h.Saved, style));
    }

    public static IReadOnlyList<IReadOnlyList<Button>> Buttons(long id, bool hasSuggestion, short? vote, string? filedIn, bool saved = false, ButtonStyle? style = null)
    {
        style ??= ButtonStyle.Default;
        var first = new List<Button>
        {
            new(vote == 1 ? "👍 ✓" : "👍", CallbackData.Vote(id, 1)),
            new(vote == -1 ? "👎 ✓" : "👎", CallbackData.Vote(id, -1)),
        };
        if (style.CanSave)
        {
            first.Add(saved ? new Button("📌 Saved ✓", CallbackData.Noop) : new Button("📌 Save", CallbackData.Save(id)));
        }

        var rows = new List<IReadOnlyList<Button>> { first };
        if (filedIn is not null)
        {
            rows.Add(new[] { new Button(style.ToPlane ? $"✓ Filed in {filedIn}" : "✓ Saved as an idea", CallbackData.Noop) });
        }
        else if (hasSuggestion)
        {
            rows.Add(new[] { new Button(style.ToPlane ? "💡 To Plane" : "💡 Save idea", CallbackData.Idea(id)) });
        }

        return rows;
    }

    /// <summary>A call with no cost header and no price is "unknown", never counted as free.</summary>
    internal static string Spend(decimal known, int unpriced)
    {
        var money = "$" + known.ToString("0.00", CultureInfo.InvariantCulture);
        return unpriced == 0 ? money : known == 0 ? "unknown" : $"{money} and {unpriced} calls of unknown cost";
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    internal static bool IsLinkable(string url) =>
        url.Length <= MaxLinkedUrl && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string Clip(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }
}
