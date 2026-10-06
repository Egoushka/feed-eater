using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FeedEater.Text;

namespace FeedEater.Sources;

/// <summary>One entry as the feed gave it: <see cref="Html"/> unchanged, <see cref="PublishedAt"/> null when the entry has no usable date.</summary>
public sealed record ParsedEntry(string Guid, string Url, string Title, string Html, DateTime? PublishedAt);

public sealed record ParsedFeed(string Title, string? SiteUrl, IReadOnlyList<ParsedEntry> Entries);

/// <summary>RSS 2.0, Atom and JSON Feed. Anything else, and malformed XML, is not a feed.</summary>
public static partial class FeedParser
{
    /// <summary>Newest entries kept per fetch; a feed that lists thousands is read for its latest ones.</summary>
    public const int MaxEntries = 200;

    /// <summary>
    /// Longer links are skipped: canonical_url sits in a btree index whose rows are capped near 2.7 KB, and a feed that sends one is
    /// not worth a failed insert. Titles are clipped, not skipped.
    /// </summary>
    public const int MaxLinkChars = 2000;

    public const int MaxTitleChars = 500;

    private static readonly Dictionary<string, string> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EST"] = "-0500", ["EDT"] = "-0400", ["CST"] = "-0600", ["CDT"] = "-0500", ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700",
    };

    public static bool TryParse(string body, Uri feedUrl, out ParsedFeed feed)
    {
        feed = null!;
        var text = body.TrimStart('﻿', ' ', '\t', '\r', '\n');
        try
        {
            var parsed = text.StartsWith('{') ? FromJson(text, feedUrl) : FromXml(text, feedUrl);
            if (parsed is null)
            {
                return false;
            }

            feed = parsed;
            return true;
        }
        catch (Exception ex) when (ex is XmlException or JsonException or InvalidOperationException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static ParsedFeed? FromXml(string text, Uri feedUrl)
    {
        var root = SafeXml.Root(ControlChars().Replace(text, ""));
        return root?.Name.LocalName switch
        {
            "rss" when root.Element("channel") is { } channel => Rss(channel, feedUrl),
            "feed" => Atom(root, feedUrl),
            _ => null,
        };
    }

    private static ParsedFeed Rss(XElement channel, Uri feedUrl)
    {
        var entries = channel.Elements("item").Select(item =>
        {
            var link = Own(item, "link");
            var guid = Own(item, "guid");
            var permaLink = !string.Equals(item.Element("guid")?.Attribute("isPermaLink")?.Value, "false", StringComparison.OrdinalIgnoreCase);
            var url = link.Length > 0 ? link : permaLink ? guid : "";
            var html = Child(item, "encoded") is { Length: > 0 } encoded ? encoded : Own(item, "description");
            return Entry(feedUrl, guid, url, Own(item, "title"), html, Own(item, "pubDate") is { Length: > 0 } date ? date : Child(item, "date"));
        });
        return new ParsedFeed(Plain(Own(channel, "title")), Absolute(feedUrl, Own(channel, "link")), Newest(entries));
    }

    private static ParsedFeed Atom(XElement feed, Uri feedUrl)
    {
        var entries = feed.Elements().Where(e => e.Name.LocalName == "entry").Select(entry =>
        {
            var url = AtomLink(entry) ?? "";
            var body = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "content") ?? entry.Elements().FirstOrDefault(e => e.Name.LocalName == "summary");
            var html = body is null ? "" : body.Attribute("type")?.Value == "xhtml" ? string.Concat(body.Nodes()) : body.Value;
            return Entry(
                feedUrl, Child(entry, "id"), url, Child(entry, "title"), html, Child(entry, "published") is { Length: > 0 } date ? date : Child(entry, "updated"));
        });
        return new ParsedFeed(Plain(Child(feed, "title")), Absolute(feedUrl, AtomLink(feed) ?? ""), Newest(entries));
    }

    private static ParsedFeed? FromJson(string text, Uri feedUrl)
    {
        var root = Json.Parse(text);
        if (root.ValueKind != JsonValueKind.Object || !(Json.Str(root, "version") ?? "").Contains("jsonfeed", StringComparison.Ordinal))
        {
            return null;
        }

        var entries = root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item =>
            {
                var url = Json.Str(item, "url") ?? Json.Str(item, "external_url") ?? "";
                var html = Json.Str(item, "content_html") ?? (Json.Str(item, "content_text") is { } plain ? WebUtility.HtmlEncode(plain) : Json.Str(item, "summary") ?? "");
                return Entry(feedUrl, Json.Str(item, "id") ?? "", url, Json.Str(item, "title") ?? "", html, Json.Str(item, "date_published") ?? Json.Str(item, "date_modified") ?? "");
            })
            : Enumerable.Empty<ParsedEntry?>();
        return new ParsedFeed(Plain(Json.Str(root, "title") ?? ""), Absolute(feedUrl, Json.Str(root, "home_page_url") ?? ""), Newest(entries));
    }

    /// <summary>Null when the entry has no http(s) link, or one over <see cref="MaxLinkChars"/>: it cannot be opened, deduplicated by URL or shown.</summary>
    private static ParsedEntry? Entry(Uri feedUrl, string guid, string url, string title, string html, string date)
    {
        if (Absolute(feedUrl, url) is not { Length: <= MaxLinkChars } absolute)
        {
            return null;
        }

        guid = Clean(guid).Trim();
        return new ParsedEntry(guid.Length > 0 ? guid : absolute, absolute, Plain(title), Clean(html), Date(date));
    }

    private static List<ParsedEntry> Newest(IEnumerable<ParsedEntry?> entries) => entries
        .OfType<ParsedEntry>()
        .OrderByDescending(e => e.PublishedAt ?? DateTime.MaxValue)
        .Take(MaxEntries)
        .ToList();

    /// <summary>RSS's own elements have no namespace, which keeps <c>atom:link</c> out of the way.</summary>
    private static string Own(XElement parent, string name) => parent.Element(name)?.Value.Trim() ?? "";

    /// <summary>An element by local name in any namespace (Atom, content:encoded, dc:date).</summary>
    private static string Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? "";

    /// <summary>The alternate link (or the first one without a rel) of an Atom feed or entry.</summary>
    private static string? AtomLink(XElement parent)
    {
        var links = parent.Elements().Where(e => e.Name.LocalName == "link" && e.Attribute("href") is not null).ToList();
        var pick = links.FirstOrDefault(l => l.Attribute("rel") is null or { Value: "alternate" }) ?? links.FirstOrDefault();
        return pick?.Attribute("href")?.Value.Trim();
    }

    private static string? Absolute(Uri baseUri, string link) =>
        link.Length > 0 && Uri.TryCreate(baseUri, link, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    private static string Plain(string title) => Clip(Clean(HtmlText.ToPlain(title)));

    /// <summary>Text from a feed goes into Postgres, which rejects NUL, and Npgsql cannot encode an unpaired surrogate; a feed with either would fail every insert.</summary>
    private static string Clean(string text) => Unstorable().Replace(text, "");

    private static string Clip(string title) =>
        title.Length <= MaxTitleChars ? title : char.IsHighSurrogate(title[MaxTitleChars - 1]) ? title[..(MaxTitleChars - 1)] : title[..MaxTitleChars];

    /// <summary>RFC 822 and ISO 8601; null when unreadable. A date without a zone is taken as UTC.</summary>
    internal static DateTime? Date(string text)
    {
        text = WeekdayPrefix().Replace(text.Trim(), "");
        foreach (var (zone, offset) in ZoneOffsets)
        {
            if (text.EndsWith(" " + zone, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^zone.Length] + offset;
                break;
            }
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime : null;
    }

    /// <summary>Control characters XML 1.0 forbids; feeds with one are common and the text around it is fine.</summary>
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.None, 250)]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]", RegexOptions.None, 250)]
    private static partial Regex Unstorable();

    [GeneratedRegex(@"^[A-Za-z]{3,9},\s*", RegexOptions.None, 250)]
    private static partial Regex WeekdayPrefix();
}
