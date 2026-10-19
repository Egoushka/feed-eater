using Microsoft.Extensions.Options;
using FeedEater.Fetch;
using FeedEater.Storage;

namespace FeedEater.Sources;

/// <summary>
/// The full text of a short item, for the read step: the article page through the safe fetcher and <see cref="ReadableText"/>, stored
/// as the item's content. The built-in reader's counterpart of the Miniflux fetch-content call.
/// </summary>
public sealed class ArticleText(ItemStore items, SafeFetcher fetcher, IOptions<FeedEaterOptions> options, ILogger<ArticleText> logger)
{
    /// <summary>The page text when it is longer than the feed's own, otherwise the feed text; a page that cannot be fetched is not an error.</summary>
    public async Task<string> FullTextAsync(Candidate c, CancellationToken ct) => (await FetchAsync(c, ct)).Text;

    /// <summary>As <see cref="FullTextAsync"/>, saying whether a fetch that was attempted failed.</summary>
    public async Task<FullText> FetchAsync(Candidate c, CancellationToken ct)
    {
        if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri))
        {
            return new(c.Content, true);
        }

        var result = await fetcher.FetchAsync(uri, ct);
        if (!result.Ok)
        {
            logger.LogDebug("Full text for item {Item} unavailable: {Outcome} {Detail}; using the feed text", c.Id, result.Outcome, result.Detail);
            return new(c.Content, false);
        }

        var text = ReadableText.Extract(result.Body!, options.Value.Caps.ReadChars);
        if (text.Length <= c.Content.Length)
        {
            return new(c.Content, true);
        }

        await items.SetContentAsync(c.Id, text, ct);
        return new(text, true);
    }
}
