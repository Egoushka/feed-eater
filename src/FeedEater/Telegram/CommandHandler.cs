using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Loops;
using FeedEater.Search;

namespace FeedEater.Telegram;

/// <summary>
/// /digest and /digest resend, /search text, and plain text as a search, from the allowed user only; anyone else is ignored
/// without a reply. Each search hit is its own message so it carries its own 👍 👎 💡 buttons.
/// </summary>
public sealed class CommandHandler(TelegramClient telegram, DigestTrigger trigger, QuietHours quiet, ArchiveSearch search, IOptions<FeedEaterOptions> options)
{
    private const int MaxResults = 5;
    private const int MaxQuery = 300;

    public async Task HandleAsync(TgMessage message, CancellationToken ct)
    {
        if (message.FromId != options.Value.Telegram.AllowedUserId || message.Text?.Trim() is not { Length: > 0 } text)
        {
            return;
        }

        if (!text.StartsWith('/'))
        {
            await SearchAsync(message, text, ct);
            return;
        }

        var words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (words[0].Split('@')[0])
        {
            case "/digest":
                await DigestAsync(message, words.Length == 2 ? words[1] : null, ct);
                break;
            case "/quiet":
                await QuietAsync(message, words.Length == 2 ? words[1] : null, ct);
                break;
            case "/search":
                if (words.Length < 2)
                {
                    await ReplyAsync(message, "Usage: /search words to look for. Any message without a command is a search too.", ct);
                    return;
                }

                await SearchAsync(message, words[1], ct);
                break;
        }
    }

    private async Task DigestAsync(TgMessage message, string? argument, CancellationToken ct)
    {
        if (argument is not null && argument != "resend")
        {
            await ReplyAsync(message, "Usage: /digest, or /digest resend to send today's digest again.", ct);
            return;
        }

        var resend = argument is not null;
        await trigger.RequestAsync(resend, ct);
        await ReplyAsync(message, resend ? "Queued: today's digest will be sent again within a minute." : "Queued: the digest starts within a minute.", ct);
    }

    private async Task QuietAsync(TgMessage message, string? argument, CancellationToken ct)
    {
        var status = argument switch
        {
            null => await quiet.ToggleAsync(ct),
            "on" => await quiet.SetAsync(true, ct),
            "off" => await quiet.SetAsync(false, ct),
            "status" => await quiet.StatusAsync(ct),
            _ => null,
        };
        await ReplyAsync(message, status is null
            ? "Usage: /quiet toggles, or /quiet on, /quiet off, /quiet status."
            : $"{WebUtility.HtmlEncode(status.Text)}{(status.Quiet ? " The digest, the weekly review and release alerts wait; /digest still works." : "")}", ct);
    }

    private async Task SearchAsync(TgMessage message, string query, CancellationToken ct)
    {
        query = query.Length <= MaxQuery ? query : query[..MaxQuery];
        var hits = await search.SearchAsync(query, null, null, null, null, MaxResults, ct);
        if (hits.Count == 0)
        {
            await ReplyAsync(message, $"Nothing found for {WebUtility.HtmlEncode(query.Length <= 80 ? query : query[..80] + "…")}.", ct);
            return;
        }

        var zone = options.Value.Zone;
        foreach (var hit in hits)
        {
            var published = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hit.PublishedAt, DateTimeKind.Utc), zone).ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            await telegram.SendAsync(message.ChatId, DigestFormatter.Result(hit, published), ct);
        }
    }

    private Task ReplyAsync(TgMessage message, string html, CancellationToken ct) => telegram.SendAsync(message.ChatId, new OutMessage(html), ct);
}
