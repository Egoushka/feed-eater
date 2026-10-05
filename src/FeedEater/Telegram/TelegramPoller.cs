using System.Globalization;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Telegram;

/// <summary>
/// Long-polls button presses and /digest commands. Updates are handled one at a time, so a double tap on 💡 sees the first filing.
/// The offset advances past a failing update so one bad callback cannot block the rest.
/// </summary>
public sealed class TelegramPoller(
    TelegramClient telegram, CallbackHandler handler, CommandHandler commands, CursorStore cursors, LoopHealth health, TimeProvider time, ILogger<TelegramPoller> logger)
    : PollingLoop(health, time, logger)
{
    private const string Cursor = "telegram:offset";
    private const int LongPollSeconds = 50;

    protected override string Name => "telegram";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

    protected override async Task PollAsync(CancellationToken ct)
    {
        var offset = long.TryParse(await cursors.GetAsync(Cursor, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        foreach (var update in await telegram.GetUpdatesAsync(offset, LongPollSeconds, ct))
        {
            if (update.Callback is { } callback)
            {
                try
                {
                    await handler.HandleAsync(callback, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Logger.LogError(ex, "Callback {Data} failed", callback.Data);
                    await AnswerQuietlyAsync(callback, ct);
                }
            }

            if (update.Message is { } message)
            {
                try
                {
                    await commands.HandleAsync(message, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Logger.LogError(ex, "Command handling failed");
                }
            }

            await cursors.SetAsync(Cursor, (update.UpdateId + 1).ToString(CultureInfo.InvariantCulture), ct);
        }
    }

    // Best effort, so the button stops spinning even when the handler failed halfway.
    private async Task AnswerQuietlyAsync(TgCallback callback, CancellationToken ct)
    {
        try
        {
            await telegram.AnswerAsync(callback.Id, "Something went wrong, try again.", ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Answering callback {Data} failed", callback.Data);
        }
    }

    internal Task TickAsync(CancellationToken ct) => PollAsync(ct);
}
