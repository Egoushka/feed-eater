using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Push;

/// <summary>
/// One ntfy message per digest, "Today: N items", whose click opens the Today page. Held while quiet hours are in effect; after a
/// failed post the next attempt waits an hour. Progress lives in cursors (<c>push:digest:{date}</c> = sent, <c>push:attempt:{date}</c>),
/// so a restart neither repeats a message nor forgets a held one.
/// </summary>
public sealed class PushNotifier(
    NtfyClient ntfy, QuietHours quiet, CursorStore cursors, DigestStore digests, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<PushNotifier> logger)
{
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <summary>True when the message for this digest has gone out (now or before).</summary>
    public async Task<bool> NotifyDigestAsync(string date, int items, CancellationToken ct)
    {
        var push = options.Value.Push;
        if (!push.Enabled)
        {
            return false;
        }

        var sentKey = $"push:digest:{date}";
        if (await cursors.GetAsync(sentKey, ct) == "sent")
        {
            return true;
        }

        if (await quiet.IsQuietAsync(ct))
        {
            logger.LogDebug("ntfy held for digest {Date}: quiet hours", date);
            return false;
        }

        var now = time.GetUtcNow();
        var attemptKey = $"push:attempt:{date}";
        if (DateTimeOffset.TryParse(await cursors.GetAsync(attemptKey, ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) && now - last < RetryAfter)
        {
            return false;
        }

        await cursors.SetAsync(attemptKey, now.ToString("O", CultureInfo.InvariantCulture), ct);
        var text = $"Today: {items.ToString(CultureInfo.InvariantCulture)} items";
        try
        {
            await ntfy.PostAsync(push.Topic, text, text, TodayUrl(), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("ntfy failed for digest {Date}: {Reason}; next attempt in an hour", date, ex is HttpRequestException h ? h.StatusCode?.ToString() ?? h.GetType().Name : "timeout");
            return false;
        }

        await cursors.SetAsync(sentKey, "sent", ct);
        logger.LogInformation("ntfy ok for digest {Date}", date);
        return true;
    }

    private string? TodayUrl()
    {
        var o = options.Value;
        var b = o.Push.BaseUrl.Length > 0 ? o.Push.BaseUrl : o.Api.BaseUrl;
        // Only an absolute http(s) address is sent as the click target; anything else leaves the message without one.
        return b.Length > 0 && Uri.TryCreate(b.TrimEnd('/') + "/ui", UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url.AbsoluteUri : null;
    }

    /// <summary>Today's digest, if it is sent but its message is held or failed: tries again (quiet hours and the hourly limit still apply).</summary>
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        var date = Schedule.Key(Schedule.Local(time.GetUtcNow(), options.Value.Zone).Date);
        if (await digests.GetAsync(date, ct) is { Status: "sent" } digest)
        {
            await NotifyDigestAsync(date, digest.ItemIds.Length, ct);
        }
    }
}
