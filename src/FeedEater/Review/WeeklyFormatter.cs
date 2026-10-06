using System.Globalization;
using System.Net;
using System.Text;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Review;

/// <summary>The Sunday review as one Telegram HTML message. Lists are capped so the visible text stays far under 4,096 characters.</summary>
public static class WeeklyFormatter
{
    private const int MaxIdeas = 8;
    private const int MaxProjects = 8;

    public static OutMessage Message(string weekOf, WeeklyReport r, TimeZoneInfo zone, string? tasteNote = null)
    {
        var from = TimeZoneInfo.ConvertTime(r.Since, zone).ToString("d MMM", CultureInfo.InvariantCulture);
        var h = new StringBuilder($"<b>Weekly review · {E(from)} to {E(DateOnly.ParseExact(weekOf, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMM", CultureInfo.InvariantCulture))}</b>\n");
        h.Append(CultureInfo.InvariantCulture, $"{r.Items} items · {r.Shown} shown in digests · 👍 {r.Up} · 👎 {r.Down} · 💡 {r.Ideas}");

        if (r.TopLiked.Count > 0)
        {
            h.Append("\n\n<b>Top 👍</b>");
            foreach (var (item, n) in r.TopLiked.Select((x, i) => (x, i + 1)))
            {
                var title = E(Clip(item.Title, 90));
                var link = IsLinkable(item.Url) ? $"<a href=\"{E(item.Url)}\">{title}</a>" : title;
                h.Append(CultureInfo.InvariantCulture, $"\n{n}. {link} <i>{E(Clip(item.Feed, 40))}</i>");
            }
        }

        if (r.Projects.Count > 0)
        {
            h.Append("\n\n<b>Rated by project</b>\n").Append(string.Join(" · ", r.Projects.Take(MaxProjects)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{E(Clip(p.Project, 30))} 👍{p.Up} 👎{p.Down}"))));
        }

        if (r.IdeasFiled.Count > 0)
        {
            h.Append("\n\n<b>Ideas filed</b>");
            foreach (var idea in r.IdeasFiled.Take(MaxIdeas))
            {
                h.Append($"\n• {E(Clip(idea.Title, 90))} ({E(idea.PlaneProject)})");
            }

            if (r.IdeasFiled.Count > MaxIdeas)
            {
                h.Append(CultureInfo.InvariantCulture, $"\n+{r.IdeasFiled.Count - MaxIdeas} more");
            }
        }

        if (r.TopFeeds.Count > 0)
        {
            h.Append("\n\n<b>Feeds that earned 👍</b>\n").Append(string.Join(" · ", r.TopFeeds.Select(f => string.Create(CultureInfo.InvariantCulture, $"{E(Clip(f.Title, 40))} {f.Count}"))));
        }

        if (r.MuteCandidates.Count > 0)
        {
            h.Append("\n\n<b>Mute candidates</b> (posts, none shown or liked)\n").Append(string.Join(" · ", r.MuteCandidates.Select(f => string.Create(CultureInfo.InvariantCulture, $"{E(Clip(f.Title, 40))} {f.Count}"))));
        }

        if (tasteNote is not null)
        {
            h.Append("\n\n<b>Ranking</b>\n").Append(E(tasteNote));
        }

        return new OutMessage(h.ToString());
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static bool IsLinkable(string url) =>
        url.Length <= 1000 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
