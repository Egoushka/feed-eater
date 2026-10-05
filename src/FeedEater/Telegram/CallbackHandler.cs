using Microsoft.Extensions.Options;
using System.Text.Json;
using FeedEater.Digest;
using FeedEater.Plane;
using FeedEater.Storage;

namespace FeedEater.Telegram;

public sealed class CallbackHandler(
    TelegramClient telegram, FeedbackStore feedback, IdeaFiler ideas, ItemStore items,
    IOptions<FeedEaterOptions> options, ILogger<CallbackHandler> logger)
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
                await telegram.AnswerAsync(callback.Id, $"Filed in {project}", ct);
                break;

            default:
                await telegram.AnswerAsync(callback.Id, null, ct);
                break;
        }
    }

    /// <summary>Also used by the web UI, so a vote means the same on both surfaces.</summary>
    public Task VoteAsync(long itemId, short value, CancellationToken ct) => feedback.SetVoteAsync(itemId, value, ct);

    public Task ClearVoteAsync(long itemId, CancellationToken ct) => feedback.ClearVoteAsync(itemId, ct);

    /// <summary>Files the item's suggestion in Plane and counts it as a 👍. The project, or the reason it was not filed.</summary>
    public async Task<(string? Project, string? Error)> FileIdeaAsync(long itemId, CancellationToken ct)
    {
        string project;
        try
        {
            project = await ideas.FileAsync(itemId, ct);
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
                DigestFormatter.Buttons(itemId, item.Suggestion is not null, (short?)item.Vote, item.FiledIn), ct);
        }
        catch (TelegramException ex) when (ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Same vote pressed twice: the keyboard already shows it.
        }
    }

    private static string Clip(string text) => text.Length <= 190 ? text : text[..189] + "…";
}
