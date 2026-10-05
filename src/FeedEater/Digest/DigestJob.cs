using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Digest;

/// <summary>Runs the digest inside the 07:30–12:00 window; a failure is reported once on Telegram and retried with backoff.</summary>
public sealed class DigestJob(
    DigestRun run, DigestStore digests, TelegramClient telegram, DigestTrigger trigger,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<DigestJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "digest";
    protected override string? DueKey(DateTime localNow) => Schedule.Window(localNow, Settings.DigestAt, Settings.DigestGiveUpAt);

    /// <summary>A pending force request (see <see cref="DigestTrigger"/>) runs now, window or not; it is cleared after the run, failed or not.</summary>
    protected override async Task PollAsync(CancellationToken ct)
    {
        if (await trigger.PendingAsync(ct) is not { } request)
        {
            await base.PollAsync(ct);
            return;
        }

        try
        {
            var outcome = await run.ForceAsync(request.Date, request.Resend, ct);
            await trigger.FinishAsync(outcome.Text, ct);
            if (!outcome.Sent)
            {
                await NotifyAsync(outcome.Text, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await FailAsync(request.Date, ex, ct);
            await trigger.FinishAsync($"Failed: {ex.Message}", ct);
            throw;
        }
    }

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        try
        {
            await run.RunAsync(key, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await FailAsync(key, ex, ct);
            throw;
        }
    }

    private async Task FailAsync(string key, Exception ex, CancellationToken ct)
    {
        var firstFailure = (await digests.GetAsync(key, ct))?.Error is null;
        await digests.SetErrorAsync(key, ex.Message, ct);
        if (firstFailure)
        {
            await ReportAsync(key, ex.Message, ct);
        }
    }

    private async Task NotifyAsync(string text, CancellationToken ct)
    {
        try
        {
            await telegram.SendAsync(Settings.Telegram.AllowedUserId, new OutMessage(WebUtility.HtmlEncode(text)), ct);
        }
        catch (Exception notify) when (notify is HttpRequestException or TelegramException or JsonException)
        {
            Logger.LogWarning(notify, "Could not send the digest notice on Telegram");
        }
    }

    private async Task ReportAsync(string key, string error, CancellationToken ct)
    {
        var until = Settings.DigestGiveUpAt.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        try
        {
            await telegram.SendAsync(Settings.Telegram.AllowedUserId,
                new OutMessage($"Digest {key} failed: {WebUtility.HtmlEncode(error)}. Retrying until {until}."), ct);
        }
        catch (Exception notify) when (notify is HttpRequestException or TelegramException or JsonException)
        {
            Logger.LogWarning(notify, "Could not report the digest failure on Telegram");
        }
    }
}
