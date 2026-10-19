using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Storage;
using FeedEater.Text;

namespace FeedEater.Sources;

/// <summary>The text, and false when a fetch was attempted and failed so the text is only the feed's own.</summary>
public sealed record FullText(string Text, bool Fetched);

/// <summary>
/// The full text of an item: the stored content when it is long enough, otherwise the article page (built-in reader) or the Miniflux
/// fetch-content call, stored as the item's content when it is longer. A fetch that fails is not an error: the feed text is returned.
/// </summary>
public sealed class ItemText(ItemStore items, IOptions<FeedEaterOptions> options, MinifluxClient miniflux, ILogger<ItemText> logger, ArticleText? articles = null)
{
    /// <summary>Null when the item does not exist.</summary>
    public async Task<string?> FullTextAsync(long itemId, CancellationToken ct) =>
        await items.GetCandidateAsync(itemId, ct) is { } c ? await FullTextAsync(c, ct) : null;

    public async Task<string> FullTextAsync(Candidate c, CancellationToken ct) => (await FetchFullTextAsync(c, ct)).Text;

    /// <summary>Null when the item does not exist.</summary>
    public async Task<FullText?> FetchFullTextAsync(long itemId, CancellationToken ct) =>
        await items.GetCandidateAsync(itemId, ct) is { } c ? await FetchFullTextAsync(c, ct) : null;

    public async Task<FullText> FetchFullTextAsync(Candidate c, CancellationToken ct)
    {
        if (articles is not null)
        {
            return c.Content.Length >= options.Value.Caps.ShortContentChars ? new(c.Content, true) : await articles.FetchAsync(c, ct);
        }

        if (c.Content.Length >= options.Value.Caps.ShortContentChars || !options.Value.Miniflux.Enabled || c.MinifluxEntryId is not { } entryId)
        {
            return new(c.Content, true);
        }

        try
        {
            var text = HtmlText.ToPlain(await miniflux.FetchContentAsync(entryId, ct));
            if (text.Length <= c.Content.Length)
            {
                return new(c.Content, true);
            }

            await items.SetContentAsync(c.Id, text, ct);
            return new(text, true);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Full text for item {Item} unavailable; using the feed text", c.Id);
            return new(c.Content, false);
        }
    }
}
