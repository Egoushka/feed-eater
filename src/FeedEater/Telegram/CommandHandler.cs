using Microsoft.Extensions.Options;
using FeedEater.Digest;

namespace FeedEater.Telegram;

/// <summary>/digest and /digest resend from the allowed user only; anyone else is ignored without a reply.</summary>
public sealed class CommandHandler(TelegramClient telegram, DigestTrigger trigger, IOptions<FeedEaterOptions> options)
{
    public async Task HandleAsync(TgMessage message, CancellationToken ct)
    {
        if (message.FromId != options.Value.Telegram.AllowedUserId || message.Text is not { } text)
        {
            return;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is 0 or > 2 || words[0].Split('@')[0] != "/digest")
        {
            return;
        }

        if (words.Length == 2 && words[1] != "resend")
        {
            await ReplyAsync(message, "Usage: /digest, or /digest resend to send today's digest again.", ct);
            return;
        }

        var resend = words.Length == 2;
        await trigger.RequestAsync(resend, ct);
        await ReplyAsync(message, resend ? "Queued: today's digest will be sent again within a minute." : "Queued: the digest starts within a minute.", ct);
    }

    private Task ReplyAsync(TgMessage message, string text, CancellationToken ct) => telegram.SendAsync(message.ChatId, new OutMessage(text), ct);
}
