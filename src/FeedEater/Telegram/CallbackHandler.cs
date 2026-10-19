using Microsoft.Extensions.Options;
using System.Text.Json;
using FeedEater.Digest;
using FeedEater.Duels;
using FeedEater.Follow;
using FeedEater.Library;
using FeedEater.Plane;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.WeeklyIdeas;

namespace FeedEater.Telegram;

public enum SaveOutcome { Saved, AlreadySaved, NoItem }

public sealed class CallbackHandler(
    TelegramClient telegram, FeedbackStore feedback, IdeaFiler ideas, ItemStore items, DuelStore duels, LibraryService library,
    IOptions<FeedEaterOptions> options, ILogger<CallbackHandler> logger, StoryFollower? follower = null, IdeaDecisions? decisions = null)
{
    public async Task HandleAsync(TgCallback callback, CancellationToken ct)
    {
        if (callback.FromId != options.Value.Telegram.AllowedUserId)
        {
            await telegram.AnswerAsync(callback.Id, "Not for you.", ct);
            return;
        }

        switch (CallbackData.Parse(callback.Data))
        {
            case VoteCallback vote:
                await VoteAsync(vote.ItemId, vote.Value, ct);
                await RefreshButtonsAsync(callback, vote.ItemId, ct);
                await telegram.AnswerAsync(callback.Id, vote.Value > 0 ? "👍 saved" : "👎 saved", ct);
                break;

            case IdeaCallback idea:
                var (project, error) = await FileIdeaAsync(idea.ItemId, ct);
                if (project is null)
                {
                    await telegram.AnswerAsync(callback.Id, Clip($"Not filed: {error}"), ct);
                    return;
                }

                await RefreshButtonsAsync(callback, idea.ItemId, ct);
                await telegram.AnswerAsync(callback.Id, ideas.Sink.Filed(project), ct);
                break;

            case SaveCallback save:
                var outcome = await SaveAsync(save.ItemId, "telegram", ct);
                if (outcome is SaveOutcome.Saved or SaveOutcome.AlreadySaved)
                {
                    await RefreshButtonsAsync(callback, save.ItemId, ct);
                }

                await telegram.AnswerAsync(callback.Id, SaveMessage(outcome), ct);
                break;

            case DuelCallback duel:
                await DuelAsync(callback, duel, ct);
                break;

            case FollowCallback follow when follower is not null:
                await telegram.AnswerAsync(callback.Id, FollowMessage(await follower.StartAsync(follow.ItemId, ct), options.Value.Follow), ct);
                break;

            case UnfollowCallback stop when follower is not null:
                await telegram.AnswerAsync(callback.Id, await follower.CloseAsync(stop.FollowId, null, ct) ? "Stopped following" : "Not following this any more", ct);
                break;

            case IdeaDecisionCallback d when decisions is not null:
                var r = d.Keep ? await decisions.KeepAsync(d.InboxId, ct) : await decisions.DropAsync(d.InboxId, ct);
                await telegram.AnswerAsync(callback.Id, r.Outcome switch
                {
                    IdeaOutcome.Filed => $"Filed in {r.Project}",
                    IdeaOutcome.Dropped => "Dropped",
                    IdeaOutcome.AlreadyDecided => "Already decided",
                    IdeaOutcome.Failed => Clip($"Not filed: {r.Error}"),
                    _ => "No such idea",
                }, ct);
                break;

            default:
                await telegram.AnswerAsync(callback.Id, null, ct);
                break;
        }
    }

    private async Task DuelAsync(TgCallback callback, DuelCallback duel, CancellationToken ct)
    {
        var answer = await duels.AnswerAsync(duel.DuelId, duel.Pick, ct);
        if (answer.Outcome != DuelOutcome.Answered)
        {
            await telegram.AnswerAsync(callback.Id, answer.Outcome == DuelOutcome.AlreadyAnswered ? "Already answered" : "No such duel", ct);
            return;
        }

        try
        {
            await telegram.EditTextAsync(callback.ChatId, callback.MessageId, DuelFormatter.Picked(answer.Title), ct);
        }
        catch (TelegramException ex)
        {
            // The votes are stored; a message too old to edit only keeps its buttons, and a second tap is ignored.
            logger.LogWarning(ex, "Editing duel {Duel} failed", duel.DuelId);
        }

        await telegram.AnswerAsync(callback.Id, answer.Title is null ? "Skipped" : "👍 saved", ct);
    }

    /// <summary>Also used by the web UI, so a vote means the same on both surfaces.</summary>
    public Task VoteAsync(long itemId, short value, CancellationToken ct) => feedback.SetVoteAsync(itemId, value, ct);

    /// <summary>Saves the item into the library, once. Also used by the web UI, which passes its own <paramref name="source"/>.</summary>
    public async Task<SaveOutcome> SaveAsync(long itemId, string source, CancellationToken ct) => (await library.SaveAsync(itemId, source, ct)).Status switch
    {
        SaveStatus.Saved => SaveOutcome.Saved,
        SaveStatus.AlreadySaved => SaveOutcome.AlreadySaved,
        _ => SaveOutcome.NoItem,
    };

    public static string SaveMessage(SaveOutcome outcome) => outcome switch
    {
        SaveOutcome.Saved => "📌 Saved to your library",
        SaveOutcome.AlreadySaved => "Already in your library",
        _ => "No such item",
    };

    public static string FollowMessage(FollowOutcome outcome, FollowOptions limits) => outcome switch
    {
        FollowOutcome.Started => $"🧵 Following for {limits.Days} days",
        FollowOutcome.AlreadyFollowing => "Already following this story",
        FollowOutcome.TooMany => $"{limits.MaxActive} stories are followed already; stop one with /follows",
        FollowOutcome.Off => "Following is off",
        _ => "No such item",
    };

    public Task ClearVoteAsync(long itemId, CancellationToken ct) => feedback.ClearVoteAsync(itemId, ct);

    /// <summary>Files the item's suggestion in Plane and counts it as a 👍. The project, or the reason it was not filed.</summary>
    public Task<(string? Project, string? Error)> FileIdeaAsync(long itemId, CancellationToken ct) => FileIdeaAsync(itemId, null, null, ct);

    /// <summary>As above, in another Plane project or with his own wording; used by replies to an item.</summary>
    public async Task<(string? Project, string? Error)> FileIdeaAsync(long itemId, string? planeProject, string? text, CancellationToken ct)
    {
        string project;
        try
        {
            project = await ideas.FileAsync(itemId, planeProject, text, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException or JsonException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Filing item {Item} failed", itemId);
            return (null, ex.Message);
        }

        await feedback.SetVoteAsync(itemId, 1, ct);
        return (project, null);
    }

    private async Task RefreshButtonsAsync(TgCallback callback, long itemId, CancellationToken ct)
    {
        if (await items.GetAsync(itemId, ct) is not { } item)
        {
            return;
        }

        // A followed story's updates carry no 🧵, and a vote must not add one.
        var follow = options.Value.Follow.Enabled && !(follower is not null && await follower.FollowsAsync(itemId, ct));
        try
        {
            await telegram.EditButtonsAsync(callback.ChatId, callback.MessageId,
                DigestFormatter.Buttons(itemId, (short?)item.Vote, item.FiledIn, item.Saved, ButtonStyle.From(options.Value), follow), ct);
        }
        catch (TelegramException ex) when (ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Same vote pressed twice: the keyboard already shows it.
        }
    }

    private static string Clip(string text) => text.Length <= 190 ? text : text[..189] + "…";
}
