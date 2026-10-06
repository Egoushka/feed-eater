using System.Text;
using System.Xml;
using System.Xml.Linq;
using FeedEater.Storage;

namespace FeedEater.Sources;

public sealed record OpmlFeed(string Title, string FeedUrl, string? SiteUrl, string? Category);

/// <summary>Subscriptions as OPML 2.0. A folder (an outline with children and no feed URL) is the category of the feeds inside it.</summary>
public static class Opml
{
    public const int MaxFeeds = 2000;

    /// <summary>Feeds with an http(s) URL, in document order, at most <see cref="MaxFeeds"/>. Throws <see cref="XmlException"/> for malformed XML.</summary>
    public static IReadOnlyList<OpmlFeed> Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CheckCharacters = false };
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿', ' ', '\t', '\r', '\n')), settings);
        var body = XDocument.Load(reader).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
        var feeds = new List<OpmlFeed>();
        if (body is not null)
        {
            Walk(body, null, feeds);
        }

        return feeds;
    }

    private static void Walk(XElement parent, string? folder, List<OpmlFeed> feeds)
    {
        foreach (var outline in parent.Elements().Where(e => e.Name.LocalName == "outline"))
        {
            var title = Attribute(outline, "title") ?? Attribute(outline, "text");
            if (Attribute(outline, "xmlUrl") is { } url)
            {
                if (feeds.Count < MaxFeeds && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    feeds.Add(new OpmlFeed(title ?? uri.AbsoluteUri, uri.AbsoluteUri, Attribute(outline, "htmlUrl"), folder));
                }
            }
            else
            {
                Walk(outline, title ?? folder, feeds);
            }
        }
    }

    private static string? Attribute(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() is { Length: > 0 } v ? v : null;

    public static string Write(IEnumerable<Feed> feeds)
    {
        var body = new XElement("body");
        var list = feeds.Where(f => f.FeedUrl is not null).ToList();
        foreach (var f in list.Where(f => string.IsNullOrEmpty(f.Category)).OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase))
        {
            body.Add(Outline(f));
        }

        foreach (var folder in list.Where(f => !string.IsNullOrEmpty(f.Category)).GroupBy(f => f.Category!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            body.Add(new XElement("outline", new XAttribute("text", folder.Key), new XAttribute("title", folder.Key),
                folder.OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase).Select(Outline)));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("opml", new XAttribute("version", "2.0"), new XElement("head", new XElement("title", "feed-eater subscriptions")), body));
        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    private static XElement Outline(Feed f)
    {
        var outline = new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", f.Title), new XAttribute("title", f.Title), new XAttribute("xmlUrl", f.FeedUrl!));
        if (f.SiteUrl is not null)
        {
            outline.Add(new XAttribute("htmlUrl", f.SiteUrl));
        }

        return outline;
    }

    /// <summary>So the declaration says utf-8; a plain StringWriter would say utf-16.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
