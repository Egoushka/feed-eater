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
    public async Task<string> FullTextAsync(Candidate c, CancellationToken ct)
    {
        if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri))
        {
            return c.Content;
        }

        var result = await fetcher.FetchAsync(uri, ct);
        if (!result.Ok)
        {
            logger.LogDebug("Full text for item {Item} unavailable: {Outcome} {Detail}; using the feed text", c.Id, result.Outcome, result.Detail);
            return c.Content;
        }

        var text = ReadableText.Extract(result.Body!, options.Value.Caps.ReadChars);
        if (text.Length <= c.Content.Length)
        {
            return c.Content;
        }

        await items.SetContentAsync(c.Id, text, ct);
        return text;
    }
}
