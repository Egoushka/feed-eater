using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Inbox;
using FeedEater.Storage;

namespace FeedEater.Follow;

public enum FollowOutcome { Started, AlreadyFollowing, TooMany, NoItem, Off }

/// <summary>
/// Follows a story: later items that match it arrive in the inbox as follow cards, until the follow ends or reaches its limit.
/// </summary>
public sealed class StoryFollower(
    FollowStore follows, ItemStore items, IInbox inbox, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    /// <summary>A story follows a little looser than a cluster, which is built on the strictest match.</summary>
    private const double Margin = 0.04;
    private const int PerRun = 10;

    public bool Enabled => options.Value.Follow.Enabled;

    public Task<IReadOnlyList<ActiveFollow>> ActiveAsync(CancellationToken ct) => follows.ActiveAsync(ct);

    /// <summary>True when 🧵 on this item would add nothing: its story is already followed.</summary>
    public Task<bool> FollowsAsync(long itemId, CancellationToken ct) => follows.FollowsStoryAsync(itemId, ct);

    public async Task<FollowOutcome> StartAsync(long itemId, CancellationToken ct)
    {
        var o = options.Value.Follow;
        if (!o.Enabled)
        {
            return FollowOutcome.Off;
        }

        if (await items.GetAsync(itemId, ct) is not { } item)
        {
            return FollowOutcome.NoItem;
        }

        if (await follows.FollowsStoryAsync(itemId, ct))
        {
            return FollowOutcome.AlreadyFollowing;
        }

        if ((await follows.ActiveAsync(ct)).Count >= o.MaxActive)
        {
            return FollowOutcome.TooMany;
        }

        var now = time.GetUtcNow().UtcDateTime;
        await follows.StartAsync(itemId, now, now.AddDays(o.Days), ct);
        return FollowOutcome.Started;
    }

    /// <summary>Adds the items that matched since the last run to the inbox, oldest first; returns how many. A failure ends the run for this follow and the item is tried again.</summary>
    public async Task<int> SendNewAsync(ActiveFollow follow, CancellationToken ct)
    {
        var o = options.Value;
        var room = Math.Min(o.Follow.MaxMessages - follow.Sent, PerRun);
        if (room <= 0)
        {
            return 0;
        }

        var sent = 0;
        foreach (var id in await follows.MatchesAsync(follow.Id, o.Cluster.Threshold - Margin, room, ct))
        {
            if (await items.GetAsync(id, ct) is null)
            {
                continue;
            }

            await inbox.AddAsync(new InboxEntry($"follow:{follow.Id}:{id}", InboxKinds.Follow, ItemId: id, RefId: follow.Id,
                Payload: new JsonObject { ["story"] = follow.Title }, ExpiresAt: time.GetUtcNow().AddDays(7)), ct);
            sent++;
            await follows.AddSentAsync(follow.Id, id, ct);
        }

        return sent;
    }

    /// <summary>Closes an active follow; false when it was not active.</summary>
    public async Task<bool> CloseAsync(long followId, CancellationToken ct) =>
        await follows.ActiveAsync(followId, ct) is not null && await follows.CloseAsync(followId, ct);
}
