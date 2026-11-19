using Microsoft.Extensions.Options;
using FeedEater.Loops;

namespace FeedEater.Push;

/// <summary>Releases an ntfy message that quiet hours held or that failed; the hourly limit is the notifier's.</summary>
public sealed class PushJob(PushNotifier push, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<PushJob> logger)
    : PollingLoop(health, time, logger)
{
    protected override string Name => "push";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override Task PollAsync(CancellationToken ct) => options.Value.Push.Enabled ? push.RetryPendingAsync(ct) : Task.CompletedTask;
}
