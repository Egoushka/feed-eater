using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Llm;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Follow;

public enum FollowOutcome { Started, AlreadyFollowing, TooMany, NoItem, Off }

/// <summary>
/// Follows a story: the root message, later items as replies to it, and the close with a summary. Everything goes to the allowed user's chat.
/// The model only writes the summary; when it fails the close still sends the links.
/// </summary>
public sealed class StoryFollower(
    FollowStore follows, ItemStore items, TelegramClient telegram, LiteLlmClient llm, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<StoryFollower> logger)
{
    /// <summary>A story follows a little looser than a cluster, which is built on the strictest match.</summary>
    private const double Margin = 0.04;
    private const int PerRun = 10;
    private const int Members = 12;
    private const int SummaryMaxTokens = 300;
    private const int MemberChars = 400;

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
        var id = await follows.StartAsync(itemId, now, now.AddDays(o.Days), ct);
        try
        {
            await follows.SetRootMessageAsync(id, await telegram.SendAsync(options.Value.Telegram.AllowedUserId, FollowFormatter.Root(item.Title, item.Url, o.Days, id), ct), ct);
        }
        catch
        {
            await follows.DeleteAsync(id, default);   // a follow without its root message would reply to nothing
            throw;
        }

        return FollowOutcome.Started;
    }

    /// <summary>Sends the items that matched since the last run, oldest first, one reply each; returns how many. Any other failed send ends the run for this follow and the item is tried again.</summary>
    public async Task<int> SendNewAsync(ActiveFollow follow, CancellationToken ct)
    {
        var o = options.Value;
        var room = Math.Min(o.Follow.MaxMessages - follow.Sent, PerRun);
        if (room <= 0)
        {
            return 0;
        }

        var style = ButtonStyle.From(o);
        var sent = 0;
        foreach (var id in await follows.MatchesAsync(follow.Id, o.Cluster.Threshold - Margin, room, ct))
        {
            if (await items.GetAsync(id, ct) is not { } item)
            {
                continue;
            }

            try
            {
                await telegram.SendAsync(o.Telegram.AllowedUserId, FollowFormatter.Update(item, style), ct, follow.RootMessageId);
                sent++;
            }
            catch (TelegramException ex) when (ex.Permanent)
            {
                logger.LogWarning(ex, "Telegram refused item {Item} of follow {Follow} for good; recorded as sent and skipped", id, follow.Id);   // else it blocks every later item
            }

            await follows.AddSentAsync(follow.Id, id, ct);
        }

        return sent;
    }

    /// <summary>Closes an active follow with its summary and links; false when it was not active. A failed send leaves it active to be tried again.</summary>
    public async Task<bool> CloseAsync(long followId, string? note, CancellationToken ct)
    {
        if (await follows.ActiveAsync(followId, ct) is not { } follow || !await follows.CloseAsync(followId, ct))
        {
            return false;
        }

        try
        {
            var members = await follows.MembersAsync(followId, Members, ct);
            var summary = follow.Sent > 0 ? await SummaryAsync(followId, members, ct) : null;
            await telegram.SendAsync(options.Value.Telegram.AllowedUserId, FollowFormatter.Closed(follow, summary, members, note), ct, follow.RootMessageId);
        }
        catch
        {
            await follows.ReopenAsync(followId, default);
            throw;
        }

        return true;
    }

    private async Task<string?> SummaryAsync(long followId, IReadOnlyList<FollowMember> members, CancellationToken ct)
    {
        var (system, user) = Prompts.FollowSummary(members.Select(m => DigestFormatter.Clip($"{m.Title}: {m.Summary}", MemberChars)).ToList());
        try
        {
            var reply = await llm.ChatAsync(options.Value.Llm.ReadModel, system, user, SummaryMaxTokens, "follow", ct);
            return reply.Content.Trim() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is BudgetExceededException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Summary of follow {Follow} failed; sending the links only", followId);
            return null;
        }
    }
}
