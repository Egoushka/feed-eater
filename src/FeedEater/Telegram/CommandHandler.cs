using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Ranking;
using FeedEater.Search;

namespace FeedEater.Telegram;

/// <summary>
/// /digest and /digest resend, /search text, /ask question, /quiet, /learn, plain text as a search (or, ending in "?", a question),
/// and replies to an item, from the allowed user only; anyone else is ignored without a reply. Each search hit is its own message so
/// it carries its own 👍 👎 💡 buttons.
/// </summary>
public sealed class CommandHandler(
    TelegramClient telegram, DigestTrigger trigger, QuietHours quiet, ArchiveSearch search, ArchiveAnswer answers, ReplyHandler replies,
    TasteSwitch taste, IOptions<FeedEaterOptions> options)
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
            if (message.ReplyToItemId is { } itemId)
            {
                await replies.HandleAsync(message.ChatId, itemId, Clip(text), ct);
            }
            else if (text.EndsWith('?'))
            {
                await AskAsync(message, text, ct);
            }
            else
            {
                await SearchAsync(message, text, ct);
            }

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
            case "/ask":
                if (words.Length < 2)
                {
                    await ReplyAsync(message, "Usage: /ask a question about what your feeds said. A message ending in ? is a question too.", ct);
                    return;
                }

                await AskAsync(message, words[1], ct);
                break;
            case "/learn":
                await LearnAsync(message, words.Length == 2 ? words[1] : null, ct);
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

    private async Task AskAsync(TgMessage message, string question, CancellationToken ct)
    {
        OutMessage answer;
        try
        {
            answer = await answers.AskAsync(Clip(question), ct);
        }
        catch (BudgetExceededException)
        {
            answer = new OutMessage("The model budget is used up; /search still works.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            answer = new OutMessage("The model is not reachable; try again later, or /search.");
        }

        await telegram.SendAsync(message.ChatId, answer, ct);
    }

    private async Task LearnAsync(TgMessage message, string? argument, CancellationToken ct)
    {
        var status = argument switch
        {
            null or "status" => await taste.StatusAsync(ct),
            "on" => await taste.SetAsync(true, ct),
            "off" => await taste.SetAsync(false, ct),
            _ => null,
        };
        await ReplyAsync(message, WebUtility.HtmlEncode(status ?? "Usage: /learn status, /learn on, /learn off. On adds the learned term to the ranking from the next digest."), ct);
    }

    private static string Clip(string text) => text.Length <= MaxQuery ? text : text[..MaxQuery];

    private async Task SearchAsync(TgMessage message, string query, CancellationToken ct)
    {
        query = Clip(query);
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
            await telegram.SendAsync(message.ChatId, DigestFormatter.Result(hit, published, ButtonStyle.From(options.Value)), ct);
        }
    }

    private Task ReplyAsync(TgMessage message, string html, CancellationToken ct) => telegram.SendAsync(message.ChatId, new OutMessage(html), ct);
}
