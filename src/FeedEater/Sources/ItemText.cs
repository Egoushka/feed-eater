using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Storage;
using FeedEater.Text;

namespace FeedEater.Sources;

/// <summary>
/// The full text of an item: the stored content when it is long enough, otherwise the article page (built-in reader) or the Miniflux
/// fetch-content call, stored as the item's content when it is longer. A fetch that fails is not an error: the feed text is returned.
/// </summary>
public sealed class ItemText(ItemStore items, IOptions<FeedEaterOptions> options, MinifluxClient miniflux, ILogger<ItemText> logger, ArticleText? articles = null)
{
    /// <summary>Null when the item does not exist.</summary>
    public async Task<string?> FullTextAsync(long itemId, CancellationToken ct) =>
        await items.GetCandidateAsync(itemId, ct) is { } c ? await FullTextAsync(c, ct) : null;

    public async Task<string> FullTextAsync(Candidate c, CancellationToken ct)
    {
        if (articles is not null)
        {
            return c.Content.Length >= options.Value.Caps.ShortContentChars ? c.Content : await articles.FullTextAsync(c, ct);
        }

        if (c.Content.Length >= options.Value.Caps.ShortContentChars || !options.Value.Miniflux.Enabled || c.MinifluxEntryId is not { } entryId)
        {
            return c.Content;
        }

        try
        {
            var text = HtmlText.ToPlain(await miniflux.FetchContentAsync(entryId, ct));
            if (text.Length <= c.Content.Length)
            {
                return c.Content;
            }

            await items.SetContentAsync(c.Id, text, ct);
            return text;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Full text for item {Item} unavailable; using the feed text", c.Id);
            return c.Content;
        }
    }
}
