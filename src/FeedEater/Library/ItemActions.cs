using System.Text.Json;
using FeedEater.Issues;
using FeedEater.Storage;

namespace FeedEater.Library;

public enum SaveOutcome { Saved, AlreadySaved, NoItem }

/// <summary>What a tap on an item means (vote, save, file as an idea); the web UI and the swipe page share it, so each action means the same on both.</summary>
public sealed class ItemActions(FeedbackStore feedback, IdeaFiler ideas, LibraryService library, ILogger<ItemActions> logger)
{
    public Task VoteAsync(long itemId, short value, CancellationToken ct) => feedback.SetVoteAsync(itemId, value, ct);

    public Task ClearVoteAsync(long itemId, CancellationToken ct) => feedback.ClearVoteAsync(itemId, ct);

    /// <summary>Saves the item into the library, once; <paramref name="source"/> says which surface asked.</summary>
    public async Task<SaveOutcome> SaveAsync(long itemId, string source, CancellationToken ct) => (await library.SaveAsync(itemId, source, ct)).Status switch
    {
        SaveStatus.Saved => SaveOutcome.Saved,
        SaveStatus.AlreadySaved => SaveOutcome.AlreadySaved,
        _ => SaveOutcome.NoItem,
    };

    /// <summary>Files the item's suggestion as an idea (a GitHub issue when that is the sink) and counts it as a 👍. The project, or the reason it was not filed.</summary>
    public Task<(string? Project, string? Error)> FileIdeaAsync(long itemId, CancellationToken ct) => FileIdeaAsync(itemId, null, null, ct);

    /// <summary>As above, in another project or with his own wording.</summary>
    public async Task<(string? Project, string? Error)> FileIdeaAsync(long itemId, string? target, string? text, CancellationToken ct)
    {
        string project;
        try
        {
            project = await ideas.FileAsync(itemId, target, text, ct);
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
}
