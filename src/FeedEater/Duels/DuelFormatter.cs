using System.Net;
using FeedEater.Digest;
using FeedEater.Telegram;

namespace FeedEater.Duels;

/// <summary>Telegram HTML for a duel and for its message after the tap. Every field is escaped and clipped.</summary>
public static class DuelFormatter
{
    public static OutMessage Message(long duelId, DuelItem first, DuelItem second) =>
        new($"<b>Which one would you rather read?</b>\n\n1. {Entry(first)}\n\n2. {Entry(second)}",
        [
            [
                new Button("◀ first", CallbackData.Duel(duelId, 'a')),
                new Button("second ▶", CallbackData.Duel(duelId, 'b')),
                new Button("skip", CallbackData.Duel(duelId, 's')),
            ],
        ]);

    /// <summary>The message after a tap: the buttons are gone.</summary>
    public static string Picked(string? title) => title is null ? "Skipped" : $"You picked {E(DigestFormatter.Clip(title, 300))}";

    private static string Entry(DuelItem i)
    {
        var title = E(DigestFormatter.Clip(i.Title, 300));
        var link = DigestFormatter.IsLinkable(i.Url) ? $"<a href=\"{E(i.Url)}\">{title}</a>" : title;
        return i.Feed.Length > 0 ? $"<b>{link}</b>\n<i>{E(DigestFormatter.Clip(i.Feed, 80))}</i>" : $"<b>{link}</b>";
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
