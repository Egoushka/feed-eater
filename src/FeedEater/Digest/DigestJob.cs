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
    DigestRun run, DigestStore digests, TelegramClient telegram,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<DigestJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "digest";
    protected override string? DueKey(DateTime localNow) => Schedule.Window(localNow, Settings.DigestAt, Settings.DigestGiveUpAt);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        try
        {
            await run.RunAsync(key, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var firstFailure = (await digests.GetAsync(key, ct))?.Error is null;
            await digests.SetErrorAsync(key, ex.Message, ct);
            if (firstFailure)
            {
                await ReportAsync(key, ex.Message, ct);
            }

            throw;
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
