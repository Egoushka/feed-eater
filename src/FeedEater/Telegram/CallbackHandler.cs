using Microsoft.Extensions.Options;
using System.Text.Json;
using FeedEater.Digest;
using FeedEater.Follow;
using FeedEater.Plane;
using FeedEater.Signals;
using FeedEater.Storage;

namespace FeedEater.Telegram;

public enum SaveOutcome { Saved, AlreadySaved, NotConfigured, Down, Refused, NoItem }

public sealed class CallbackHandler(
    TelegramClient telegram, FeedbackStore feedback, IdeaFiler ideas, ItemStore items, KarakeepClient karakeep,
    IOptions<FeedEaterOptions> options, ILogger<CallbackHandler> logger, StoryFollower? follower = null)
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
                var outcome = await SaveAsync(save.ItemId, ct);
                if (outcome is SaveOutcome.Saved or SaveOutcome.AlreadySaved)
                {
                    await RefreshButtonsAsync(callback, save.ItemId, ct);
                }

                await telegram.AnswerAsync(callback.Id, SaveMessage(outcome), ct);
                break;

            case FollowCallback follow when follower is not null:
                await telegram.AnswerAsync(callback.Id, FollowMessage(await follower.StartAsync(follow.ItemId, ct), options.Value.Follow), ct);
                break;

            case UnfollowCallback stop when follower is not null:
                await telegram.AnswerAsync(callback.Id, await follower.CloseAsync(stop.FollowId, null, ct) ? "Stopped following" : "Not following this any more", ct);
                break;

            default:
                await telegram.AnswerAsync(callback.Id, null, ct);
                break;
        }
    }

    /// <summary>Also used by the web UI, so a vote means the same on both surfaces.</summary>
    public Task VoteAsync(long itemId, short value, CancellationToken ct) => feedback.SetVoteAsync(itemId, value, ct);

    /// <summary>Saves the item's link as a Karakeep bookmark, once. Karakeep being down is an outcome, not an exception.</summary>
    public async Task<SaveOutcome> SaveAsync(long itemId, CancellationToken ct)
    {
        if (await feedback.IsSavedAsync(itemId, ct))
        {
            return SaveOutcome.AlreadySaved;
        }

        if (await items.GetAsync(itemId, ct) is not { } item)
        {
            return SaveOutcome.NoItem;
        }

        if (!options.Value.Karakeep.Enabled)
        {
            return SaveOutcome.NotConfigured;
        }

        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return SaveOutcome.Refused;
        }

        string id;
        try
        {
            id = await karakeep.CreateLinkAsync(item.Url, item.Title, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Saving item {Item} to Karakeep failed", itemId);
            return SaveOutcome.Down;
        }

        await feedback.AddSavedAsync(itemId, id, ct);
        return SaveOutcome.Saved;
    }

    public static string SaveMessage(SaveOutcome outcome) => outcome switch
    {
        SaveOutcome.Saved => "📌 Saved to Karakeep",
        SaveOutcome.AlreadySaved => "Already saved",
        SaveOutcome.NotConfigured => "Karakeep is not configured",
        SaveOutcome.Down => "Karakeep is not reachable; try again later",
        SaveOutcome.Refused => "This item has no link to save",
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

        try
        {
            await telegram.EditButtonsAsync(callback.ChatId, callback.MessageId,
                DigestFormatter.Buttons(itemId, item.Suggestion is not null, (short?)item.Vote, item.FiledIn, item.Saved, ButtonStyle.From(options.Value), options.Value.Follow.Enabled), ct);
        }
        catch (TelegramException ex) when (ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Same vote pressed twice: the keyboard already shows it.
        }
    }

    private static string Clip(string text) => text.Length <= 190 ? text : text[..189] + "…";
}
