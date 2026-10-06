using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Loops;

/// <summary>
/// Checks every minute whether a run is due; runs at most once per key and stores the key only after success,
/// so a failed run is retried with the polling backoff.
/// </summary>
public abstract class ScheduledJob(CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger logger)
    : PollingLoop(health, time, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);
    protected FeedEaterOptions Settings => options.Value;
    protected CursorStore Cursors => cursors;

    /// <summary>The run key for this moment, or null when nothing is due.</summary>
    protected abstract string? DueKey(DateTime localNow);

    protected abstract Task RunAsync(string key, CancellationToken ct);

    /// <summary>True to leave a due run for the next tick (quiet hours); the key is not stored, so it runs when released.</summary>
    protected virtual Task<bool> HoldAsync(CancellationToken ct) => Task.FromResult(false);

    protected override async Task PollAsync(CancellationToken ct)
    {
        var key = DueKey(Schedule.Local(Time.GetUtcNow(), options.Value.Zone));
        if (key is null || await cursors.GetAsync($"job:{Name}", ct) == key)
        {
            return;
        }

        if (await HoldAsync(ct))
        {
            return;
        }

        await RunAsync(key, ct);
        await cursors.SetAsync($"job:{Name}", key, ct);
    }

    internal Task TickAsync(CancellationToken ct) => PollAsync(ct);
}
