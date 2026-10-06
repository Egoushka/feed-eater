using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using FeedEater.Storage;
using FeedEater.Text;

namespace FeedEater.Fetch;

public sealed record SuggestedFeed(string Domain, IReadOnlyList<LikedLink> Why, CachedSuggestion? Found);

/// <summary>
/// Suggests feeds to subscribe to: domains of 👍 items (and of what 👍 Reddit and HN posts link to) that no subscribed feed covers,
/// ranked by how often they were liked. The feed URL is found on the domain's homepage through the safe fetcher, a few domains per poll,
/// each checked at most once a month. feed-eater only suggests; subscribing happens in Miniflux, which it never writes to.
/// </summary>
public sealed partial class FeedDiscoverer(
    DiscoveryStore store, ItemStore items, SafeFetcher fetcher, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<FeedDiscoverer> logger)
{
    public const int PerPoll = 3;
    public const int Shown = 10;
    private static readonly TimeSpan Recheck = TimeSpan.FromDays(30);
    private static readonly TimeSpan Window = TimeSpan.FromDays(180);

    /// <summary>Hosts too big or too generic to have "a feed" worth suggesting; Reddit and HN are subscribed through their own feeds.</summary>
    private static readonly string[] Skip = ["reddit.com", "redd.it", "ycombinator.com", "github.com", "githubusercontent.com", "imgur.com", "wikipedia.org", "archive.org"];

    public async Task<IReadOnlyList<SuggestedFeed>> SuggestionsAsync(CancellationToken ct)
    {
        var liked = await store.LikedLinksAsync(time.GetUtcNow() - Window, ct);
        var cached = await store.CachedAsync(ct);
        var subscribed = (await items.FeedsAsync(ct)).Select(f => Host(f.SiteUrl)).Where(h => h is not null).Select(h => h!).ToList();
        return Rank(liked, subscribed, options.Value.Fetch.BlockedHosts)
            .Take(Shown)
            .Select(r => new SuggestedFeed(r.Domain, r.Why, cached.GetValueOrDefault(r.Domain)))
            .ToList();
    }

    /// <summary>Looks up the feed URL of up to <see cref="PerPoll"/> top suggestions that were never checked or are a month old.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var done = 0;
        foreach (var s in await SuggestionsAsync(ct))
        {
            if (done >= PerPoll)
            {
                break;
            }

            if (s.Found is { } found && time.GetUtcNow() - found.CheckedAt < Recheck)
            {
                continue;
            }

            var result = await fetcher.FetchAsync(new Uri($"https://{s.Domain}/"), ct);
            if (result.Outcome == FetchOutcome.CapReached)
            {
                break;
            }

            var feed = result.Ok ? FindFeed(result.Body!, result.FinalUri!) : null;
            logger.LogDebug("Feed discovery for {Domain}: {Outcome}, {Feed}", s.Domain, result.Outcome, feed);
            await store.SaveAsync(s.Domain, feed, feed is not null ? "found" : result.Ok ? "none" : "failed", time.GetUtcNow(), ct);
            done++;
        }

        return done;
    }

    /// <summary>Liked domains most liked first (ties: most recent), minus subscribed, skipped and blocked ones.</summary>
    internal static IReadOnlyList<(string Domain, IReadOnlyList<LikedLink> Why)> Rank(
        IReadOnlyList<LikedLink> liked, IReadOnlyList<string> subscribedHosts, IReadOnlyList<string> blocked)
    {
        var byDomain = new Dictionary<string, List<LikedLink>>(StringComparer.Ordinal);
        foreach (var l in liked)
        {
            if ((Host(l.LinkUrl) ?? Host(l.Url)) is not { } host || Covers(host, Skip) || Covers(host, blocked) || subscribedHosts.Any(s => Same(host, s)))
            {
                continue;
            }

            if (!byDomain.TryGetValue(host, out var list))
            {
                byDomain[host] = list = [];
            }

            if (list.All(x => x.Id != l.Id))
            {
                list.Add(l);
            }
        }

        return byDomain.Select((kv, position) => (kv.Key, kv.Value, position))
            .OrderByDescending(x => x.Value.Count).ThenBy(x => x.position)
            .Select(x => (x.Key, (IReadOnlyList<LikedLink>)x.Value)).ToList();
    }

    /// <summary>The first RSS, Atom or JSON feed link in the page head, as an absolute http(s) URL.</summary>
    internal static string? FindFeed(string html, Uri page)
    {
        try
        {
            return FindFeedCore(html.Length <= HtmlText.MaxChars ? html : html[..HtmlText.MaxChars], page);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private const int MaxFeedUrl = 2000;

    private static string? FindFeedCore(string html, Uri page)
    {
        foreach (Match tag in LinkTag().Matches(html))
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Attribute().Matches(tag.Value))
            {
                attrs[m.Groups[1].Value.ToLowerInvariant()] = WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            }

            if (attrs.GetValueOrDefault("rel")?.Split(' ').Contains("alternate", StringComparer.OrdinalIgnoreCase) == true
                && attrs.GetValueOrDefault("type")?.Trim().ToLowerInvariant() is "application/rss+xml" or "application/atom+xml" or "application/feed+json"
                && attrs.GetValueOrDefault("href") is { Length: > 0 } href
                && Uri.TryCreate(page, href, out var absolute) && absolute.Scheme is "http" or "https" && absolute.AbsoluteUri.Length <= MaxFeedUrl)
            {
                return absolute.AbsoluteUri;
            }
        }

        return null;
    }

    private static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.', StringComparison.Ordinal)
            ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host).ToLowerInvariant()
            : null;

    private static bool Covers(string host, IEnumerable<string> list) =>
        list.Any(b => host.Equals(b, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + b, StringComparison.OrdinalIgnoreCase));

    /// <summary>The same site: equal hosts, or one a subdomain of the other (blog.example.com and example.com).</summary>
    private static bool Same(string a, string b) => a == b || a.EndsWith("." + b, StringComparison.Ordinal) || b.EndsWith("." + a, StringComparison.Ordinal);

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase, 250)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"([a-zA-Z-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.None, 250)]
    private static partial Regex Attribute();
}
