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
                await feedback.SetVoteAsync(vote.ItemId, vote.Value, ct);
                await RefreshButtonsAsync(callback, vote.ItemId, ct);
                await telegram.AnswerAsync(callback.Id, vote.Value > 0 ? "👍 saved" : "👎 saved", ct);
                break;

            case IdeaCallback idea:
                string project;
                try
                {
                    project = await ideas.FileAsync(idea.ItemId, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException or JsonException
                    || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    logger.LogWarning(ex, "Filing item {Item} failed", idea.ItemId);
                    await telegram.AnswerAsync(callback.Id, Clip($"Not filed: {ex.Message}"), ct);
                    return;
                }

                await feedback.SetVoteAsync(idea.ItemId, 1, ct);
                await RefreshButtonsAsync(callback, idea.ItemId, ct);
                await telegram.AnswerAsync(callback.Id, $"Filed in {project}", ct);
                break;

            default:
                await telegram.AnswerAsync(callback.Id, null, ct);
                break;
        }
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
