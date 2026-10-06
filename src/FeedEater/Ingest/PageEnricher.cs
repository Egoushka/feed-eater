using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Fetch;
using FeedEater.Storage;

namespace FeedEater.Ingest;

/// <summary>
/// For recent Reddit and Hacker News link posts with a short teaser, fetches the page they link to (and the top HN comments) so the
/// models read the real subject. Bounded per poll; each item is tried once. See docs/specs/2026-10-06-page-fetch.md.
/// </summary>
public sealed class PageEnricher(
    ItemStore items, SafeFetcher fetcher, HnClient hn, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<PageEnricher> logger)
{
    private const int CommentChars = 600;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var o = options.Value.Fetch;
        if (!o.Enabled)
        {
            return 0;
        }

        var done = 0;
        foreach (var p in await items.PendingLinksAsync(time.GetUtcNow().AddDays(-3), o.ShortChars, o.MaxPerPoll, ct))
        {
            var parts = new List<string>();
            if (p.LinkUrl is not null && Uri.TryCreate(p.LinkUrl, UriKind.Absolute, out var uri))
            {
                var result = await fetcher.FetchAsync(uri, ct);
                if (result.Outcome == FetchOutcome.CapReached)
                {
                    break;   // not marked: tomorrow's allowance picks it up
                }

                if (result.Ok && ReadableText.Extract(result.Body!, o.PageChars) is { Length: > 0 } text)
                {
                    parts.Add($"Linked page ({uri.Host}):\n{text}");
                }
                else
                {
                    logger.LogDebug("No page text for item {Item}: {Outcome} {Detail}", p.Id, result.Outcome, result.Detail);
                }
            }

            if (p.HnId is { } id && await CommentsAsync(id, o, ct) is { } comments)
            {
                parts.Add(comments);
            }

            await items.SaveExtraAsync(p.Id, parts.Count == 0 ? null : string.Join("\n\n", parts), ct);
            done++;
        }

        return done;
    }

    private async Task<string?> CommentsAsync(long id, FetchOptions o, CancellationToken ct)
    {
        try
        {
            return await hn.TopCommentsAsync(id, o.Comments, CommentChars, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "HN comments for {Id} unavailable", id);
            return null;
        }
    }
}
