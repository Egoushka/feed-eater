using FeedEater.Fetch;

namespace FeedEater.Sources;

public enum FeedReadStatus { Ok, NotModified, NotAFeed, Failed }

/// <summary><see cref="Page"/> holds the body of a non-feed answer, for finding the feed link in it; <see cref="Error"/> is a short text for /ui/sources.</summary>
public sealed record FeedRead(
    FeedReadStatus Status, ParsedFeed? Feed = null, Uri? FinalUri = null, string? ETag = null, string? LastModified = null, string? Error = null, string? Page = null);

/// <summary>One feed URL in, one parsed feed out, through the safe fetcher.</summary>
public sealed class FeedReader(SafeFetcher fetcher)
{
    public async Task<FeedRead> ReadAsync(Uri url, FeedConditions conditions, CancellationToken ct)
    {
        var result = await fetcher.FetchFeedAsync(url, conditions, ct);
        switch (result.Outcome)
        {
            case FetchOutcome.NotModified:
                return new FeedRead(FeedReadStatus.NotModified, FinalUri: result.FinalUri);
            case FetchOutcome.Ok when FeedParser.TryParse(result.Body!, result.FinalUri!, out var feed):
                return new FeedRead(FeedReadStatus.Ok, feed, result.FinalUri, result.ETag, result.LastModified);
            case FetchOutcome.Ok:
                return new FeedRead(FeedReadStatus.NotAFeed, FinalUri: result.FinalUri, Error: "not an RSS, Atom or JSON feed", Page: result.Body);
            default:
                return new FeedRead(FeedReadStatus.Failed, Error: Describe(result));
        }
    }

    private static string Describe(FetchResult r) => r.Outcome switch
    {
        FetchOutcome.Refused when r.Detail?.Contains("address", StringComparison.Ordinal) == true => "private address; list the host in Source:AllowedHosts to allow it",
        FetchOutcome.Refused => $"refused ({r.Detail})",
        FetchOutcome.TooManyRedirects => "too many redirects",
        FetchOutcome.TooLarge => "answer over 5 MB",
        FetchOutcome.Failed when int.TryParse(r.Detail, out var status) => $"HTTP {status}",
        _ => "not reachable",
    };
}
