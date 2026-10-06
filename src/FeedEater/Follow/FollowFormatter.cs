using System.Globalization;
using System.Net;
using System.Text;
using FeedEater.Digest;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Follow;

/// <summary>Telegram HTML for followed stories. Every field is escaped and clipped, like <see cref="DigestFormatter"/>.</summary>
public static class FollowFormatter
{
    /// <summary>The thread's first message; its stop button also tells a reply to it from a reply to an item.</summary>
    public static OutMessage Root(string title, string url, int days, long followId) =>
        new($"🧵 Following <b>{Linked(title, url, 300)}</b> for {days.ToString(CultureInfo.InvariantCulture)} days.",
            [[new Button("⏹ Stop following", CallbackData.Unfollow(followId))]]);

    /// <summary>One later item: title, link, one line of what is new, and the item's own buttons.</summary>
    public static OutMessage Update(ItemView item, ButtonStyle style)
    {
        var news = string.Join(' ', (string.IsNullOrWhiteSpace(item.Summary) ? item.Content : item.Summary).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var html = new StringBuilder($"🧵 <b>{Linked(item.Title, item.Url, 300)}</b>\n<i>{E(DigestFormatter.Clip(item.Feed, 80))}</i>");
        if (news.Length > 0)
        {
            html.Append("\n\n").Append(E(DigestFormatter.Clip(news, 200)));
        }

        return new OutMessage(html.ToString(), DigestFormatter.Buttons(item.Id, item.Suggestion is not null, (short?)item.Vote, item.FiledIn, item.Saved, style));
    }

    /// <summary>The close: the model's summary when there is one, then the links, then why it ended early when it did.</summary>
    public static OutMessage Closed(ActiveFollow follow, string? summary, IReadOnlyList<FollowMember> members, string? note)
    {
        var html = new StringBuilder($"🧵 <b>Story closed:</b> {E(DigestFormatter.Clip(follow.Title, 200))}");
        if (note is not null)
        {
            html.Append("\n<i>").Append(E(note)).Append("</i>");
        }

        if (follow.Sent == 0)
        {
            html.Append("\n\nNo later items matched this story.");
        }
        else
        {
            if (summary is not null)
            {
                html.Append("\n\n").Append(E(DigestFormatter.Clip(summary, 800)));
            }

            html.Append("\n");
            foreach (var m in members)
            {
                html.Append("\n• ").Append(Linked(m.Title, m.Url, 90));
            }

            var more = follow.Sent + 1 - members.Count;
            if (more > 0)
            {
                html.Append(CultureInfo.InvariantCulture, $"\n+{more} more");
            }
        }

        return new OutMessage(html.ToString());
    }

    public static OutMessage List(IReadOnlyList<ActiveFollow> follows, TimeZoneInfo zone)
    {
        if (follows.Count == 0)
        {
            return new OutMessage("You follow no stories. Tap 🧵 on an item to follow its story.");
        }

        var html = new StringBuilder("<b>Followed stories</b>");
        foreach (var f in follows)
        {
            var until = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(f.EndsAt, DateTimeKind.Utc), zone).ToString("d MMM", CultureInfo.InvariantCulture);
            html.Append(CultureInfo.InvariantCulture, $"\n• {Linked(f.Title, f.Url, 120)} · until {until} · {f.Sent} sent");
        }

        return new OutMessage(html.ToString(), follows.Select(f => (IReadOnlyList<Button>)[new Button($"⏹ {DigestFormatter.Clip(f.Title, 40)}", CallbackData.Unfollow(f.Id))]).ToList());
    }

    private static string Linked(string title, string url, int max)
    {
        var text = E(DigestFormatter.Clip(title, max));
        return DigestFormatter.IsLinkable(url) ? $"<a href=\"{E(url)}\">{text}</a>" : text;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
