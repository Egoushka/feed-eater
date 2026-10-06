using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Follow;

/// <summary>
/// Every 10 minutes: sends the new items of each followed story as replies to its root message, and closes the follows whose time or
/// message limit is up. Quiet hours hold the whole run. One follow failing to send does not stop the others.
/// </summary>
public sealed class FollowJob(
    FollowStore follows, StoryFollower follower, QuietHours quiet, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<FollowJob> logger)
    : PollingLoop(health, time, logger)
{
    protected override string Name => "follow";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);

    protected override async Task PollAsync(CancellationToken ct)
    {
        if (!await quiet.IsQuietAsync(ct))
        {
            Logger.LogInformation("Follow sent {Count} messages", await RunAsync(ct));
        }
    }

    internal async Task<int> RunAsync(CancellationToken ct)
    {
        var max = options.Value.Follow.MaxMessages;
        var sent = 0;
        foreach (var follow in await follows.ActiveAsync(ct))
        {
            try
            {
                var n = await follower.SendNewAsync(follow, ct);   // also at the end, for what arrived since the last run
                sent += n;
                if (Time.GetUtcNow().UtcDateTime >= follow.EndsAt)
                {
                    await follower.CloseAsync(follow.Id, null, ct);
                }
                else if (follow.Sent + n >= max)
                {
                    await follower.CloseAsync(follow.Id, $"Closed at the limit of {max} messages.", ct);
                }
            }
            catch (Exception ex) when (ex is TelegramException or HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                Logger.LogWarning(ex, "Follow {Follow} failed; the others go on", follow.Id);
            }
        }

        return sent;
    }

    internal Task TickAsync(CancellationToken ct) => PollAsync(ct);
}
