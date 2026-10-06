using System.Net;
using System.Text.RegularExpressions;

namespace FeedEater.Fetch;

/// <summary>
/// Finds what a link post points at. Only Reddit and Hacker News items count: the page they link to is the real subject and the
/// feed text is a teaser. An ordinary blog item is its own page, so it has no link target.
/// </summary>
public static partial class LinkExtractor
{
    private static readonly string[] Social = ["reddit.com", "redd.it", "ycombinator.com", "redditmedia.com", "redditstatic.com", "imgur.com"];

    public static (string? LinkUrl, long? HnId) Find(string itemUrl, string html)
    {
        try
        {
            return FindCore(itemUrl, html.Length <= Text.HtmlText.MaxChars ? html : html[..Text.HtmlText.MaxChars]);
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, null);
        }
    }

    private static (string? LinkUrl, long? HnId) FindCore(string itemUrl, string html)
    {
        long? hn = HnItem().Match(itemUrl + " " + html) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
        var itemHost = HostOf(itemUrl);
        if (hn is null && !Is(itemHost, "reddit.com") && !Is(itemHost, "redd.it"))
        {
            return (null, null);
        }

        if (hn is not null && itemHost is not null && !Is(itemHost, "ycombinator.com") && !Is(itemHost, "reddit.com"))
        {
            return (itemUrl, hn);   // an HN feed whose item URL is the article itself
        }

        foreach (Match href in Href().Matches(html))
        {
            var url = WebUtility.HtmlDecode(href.Groups[1].Value).Trim();
            if (HostOf(url) is { } host && !Social.Any(s => Is(host, s)))
            {
                return (url, hn);
            }
        }

        return (null, hn);
    }

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : null;

    private static bool Is(string? host, string domain) =>
        host is not null && (host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"news\.ycombinator\.com/item\?id=(\d+)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex HnItem();

    [GeneratedRegex(@"href\s*=\s*""(https?://[^""]+)""", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Href();
}
