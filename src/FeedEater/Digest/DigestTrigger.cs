using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Digest;

public sealed record ForceRequest(string Date, bool Resend);

public sealed record ForceResult(DateTimeOffset At, string Text);

/// <summary>
/// Asks <see cref="DigestJob"/> for a digest outside its window. The request is a cursor holding "run:date" or "resend:date";
/// it only counts for that local date, so a request that was never picked up cannot fire a day later.
/// </summary>
public sealed class DigestTrigger(CursorStore cursors, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    private const string RequestCursor = "digest:force";
    private const string ResultCursor = "digest:force-result";

    public string Today() => Schedule.Key(Schedule.Local(time.GetUtcNow(), options.Value.Zone).Date);

    public async Task RequestAsync(bool resend, CancellationToken ct) =>
        await cursors.SetAsync(RequestCursor, $"{(resend ? "resend" : "run")}:{Today()}", ct);

    /// <summary>The request for today, if any. A stale or malformed one is dropped.</summary>
    public async Task<ForceRequest?> PendingAsync(CancellationToken ct)
    {
        if (await cursors.GetAsync(RequestCursor, ct) is not { } value)
        {
            return null;
        }

        if (value.Split(':') is [var mode and ("run" or "resend"), var date] && date == Today())
        {
            return new ForceRequest(date, mode == "resend");
        }

        await cursors.DeleteAsync(RequestCursor, ct);
        return null;
    }

    public async Task FinishAsync(string text, CancellationToken ct)
    {
        await cursors.SetAsync(ResultCursor, $"{time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}|{text}", ct);
        await cursors.DeleteAsync(RequestCursor, ct);
    }

    public async Task<ForceResult?> LastResultAsync(CancellationToken ct) =>
        await cursors.GetAsync(ResultCursor, ct) is { } v && v.Split('|', 2) is [var at, var text]
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? new ForceResult(when, text)
            : null;
}
