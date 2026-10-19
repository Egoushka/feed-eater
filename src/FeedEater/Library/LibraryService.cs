using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Text;
using Microsoft.Extensions.Options;

namespace FeedEater.Library;

public enum SaveStatus { Saved, AlreadySaved, NoItem, BadUrl }

public sealed record SaveResult(SaveStatus Status, long? ItemId);

/// <summary>Saves are local first; Karakeep only mirrors. No model, fetch or embed call here: enrichment runs later in its own loop.</summary>
public sealed class LibraryService(LibraryStore library, ItemStore items, FeedbackStore feedback, KarakeepClient karakeep,
    IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<LibraryService> logger)
{
    public const string ImportSource = "import";
    private const int MaxUrlLength = 2048;

    public async Task<SaveResult> SaveAsync(long itemId, string source, CancellationToken ct)
    {
        var item = await items.GetAsync(itemId, ct);
        if (item is null)
        {
            return new SaveResult(SaveStatus.NoItem, null);
        }

        if (!await library.AddAsync(itemId, source, time.GetUtcNow(), ct))
        {
            return new SaveResult(SaveStatus.AlreadySaved, itemId);
        }

        if (source != ImportSource)
        {
            await feedback.SetVoteAsync(itemId, 1, ct);
        }

        await MirrorAsync(itemId, item.Url, item.Title, ct);
        return new SaveResult(SaveStatus.Saved, itemId);
    }

    public async Task<SaveResult> SaveUrlAsync(string url, string? title, string? text, string source, CancellationToken ct)
    {
        url = url.Trim();
        if (url.Length > MaxUrlLength || !IsWebUrl(url, out var uri))
        {
            return new SaveResult(SaveStatus.BadUrl, null);
        }

        var canonical = UrlCanonicalizer.Canonical(url);
        var itemId = await items.FindByCanonicalAsync(canonical, ct);
        if (itemId is null)
        {
            var name = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim();
            var sourceKey = "share:" + canonical;
            itemId = await items.InsertAsync(new NewItem(
                null, null, url, canonical, TitleHash.Of(title ?? ""), name, time.GetUtcNow().UtcDateTime, HtmlText.ToPlain(text ?? ""), SourceKey: sourceKey), ct)
                ?? await items.FindBySourceKeyAsync(sourceKey, ct);
        }

        return itemId is { } id ? await SaveAsync(id, source, ct) : new SaveResult(SaveStatus.NoItem, null);
    }

    /// <summary>Removes the library row; the vote stays (undo restores votes separately).</summary>
    public Task<bool> UnsaveAsync(long itemId, CancellationToken ct) => library.RemoveAsync(itemId, ct);

    private async Task MirrorAsync(long itemId, string url, string title, CancellationToken ct)
    {
        var settings = options.Value.Karakeep;
        if (!settings.Mirror || !settings.Enabled || !IsWebUrl(url, out _))
        {
            return;
        }

        try
        {
            await library.SetKarakeepIdAsync(itemId, await karakeep.CreateLinkAsync(url, title, ct), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(e, "Karakeep mirror of item {ItemId} failed; the save stands", itemId);
        }
    }

    private static bool IsWebUrl(string url, out Uri uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https";
}
