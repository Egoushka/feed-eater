using System.Globalization;
using System.Net;
using System.Text;
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
}

/// <summary><c>MonthSpend</c> is spend since the 1st of the current month; <c>WeekUpRate</c> is the 👍 share of the last 7 days' votes, null when there were none.</summary>
public sealed record DigestHeader(
    DateOnly Date, int Shown, int Candidates, IReadOnlyList<(string Key, int Count)> ByProject,
    int VotesUp, int VotesDown, decimal MonthSpend, double? WeekUpRate, IReadOnlyList<string> Notes);

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

        sb.Append(CultureInfo.InvariantCulture, $"\nYesterday 👍 {h.VotesUp} · 👎 {h.VotesDown} · spend this month ${h.MonthSpend.ToString("0.00", CultureInfo.InvariantCulture)}");
        if (h.WeekUpRate is { } rate)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n7-day 👍 rate {Math.Round(rate * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%");
        }

        foreach (var note in h.Notes)
        {
            sb.Append("\n⚠️ ").Append(E(Clip(note, 300)));
        }

        return new OutMessage(sb.ToString());
    }

    public static OutMessage Item(DigestItem i, short? vote, string? filedIn)
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

        return new OutMessage(html.ToString(), Buttons(i.Id, i.Suggestion is not null, vote, filedIn));
    }

    public static IReadOnlyList<IReadOnlyList<Button>> Buttons(long id, bool hasSuggestion, short? vote, string? filedIn)
    {
        var rows = new List<IReadOnlyList<Button>>
        {
            new[]
            {
                new Button(vote == 1 ? "👍 ✓" : "👍", CallbackData.Vote(id, 1)),
                new Button(vote == -1 ? "👎 ✓" : "👎", CallbackData.Vote(id, -1)),
            },
        };
        if (filedIn is not null)
        {
            rows.Add(new[] { new Button($"✓ Filed in {filedIn}", CallbackData.Noop) });
        }
        else if (hasSuggestion)
        {
            rows.Add(new[] { new Button("💡 To Plane", CallbackData.Idea(id)) });
        }

        return rows;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static bool IsLinkable(string url) =>
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
