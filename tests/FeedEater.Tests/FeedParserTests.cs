using FeedEater.Sources;

namespace FeedEater.Tests;

public sealed class FeedParserTests
{
    private static readonly Uri Url = new("https://blog.example/feed");

    private const string Rss =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:atom="http://www.w3.org/2005/Atom">
          <channel>
            <title>Blog &amp; Co</title>
            <atom:link href="https://blog.example/feed" rel="self" type="application/rss+xml"/>
            <link>https://blog.example/</link>
            <item>
              <title>First &lt;b&gt;post&lt;/b&gt;</title>
              <link>https://blog.example/first</link>
              <guid isPermaLink="false">tag:blog.example,1</guid>
              <pubDate>Tue, 06 Oct 2026 09:30:00 EST</pubDate>
              <description>short</description>
              <content:encoded><![CDATA[<p>The <em>full</em> body.</p>]]></content:encoded>
            </item>
            <item>
              <title>Second</title>
              <link>/second?utm_source=x</link>
              <description>&lt;p&gt;only a description&lt;/p&gt;</description>
              <dc:date>2026-10-05T08:00:00Z</dc:date>
            </item>
            <item><title>No link, no guid</title><description>dropped</description></item>
            <item><title>Guid is the link</title><guid>https://blog.example/third</guid></item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Reads_RSS_2_0_with_content_encoded_guids_relative_links_and_dates()
    {
        Assert.True(FeedParser.TryParse(Rss, Url, out var feed));

        Assert.Equal("Blog & Co", feed.Title);
        Assert.Equal("https://blog.example/", feed.SiteUrl);
        Assert.Equal(3, feed.Entries.Count);   // the entry with no link or guid has nothing to open
        var first = feed.Entries.Single(e => e.Title == "First post");
        Assert.Equal("tag:blog.example,1", first.Guid);
        Assert.Equal("https://blog.example/first", first.Url);
        Assert.Equal("<p>The <em>full</em> body.</p>", first.Html);
        Assert.Equal(new DateTime(2026, 10, 6, 14, 30, 0, DateTimeKind.Utc), first.PublishedAt);
        var second = feed.Entries.Single(e => e.Title == "Second");
        Assert.Equal("https://blog.example/second?utm_source=x", second.Url);
        Assert.Equal("https://blog.example/second?utm_source=x", second.Guid);   // no guid: the URL
        Assert.Equal("<p>only a description</p>", second.Html);
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), second.PublishedAt);
        Assert.Equal("https://blog.example/third", feed.Entries.Single(e => e.Title == "Guid is the link").Url);
    }

    [Fact]
    public void Entries_come_newest_first_and_undated_ones_count_as_newest()
    {
        FeedParser.TryParse(Rss, Url, out var feed);

        Assert.Equal(["Guid is the link", "First post", "Second"], feed.Entries.Select(e => e.Title));
    }

    private const string Atom =
        """
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title type="html">Atom &amp;amp; Friends</title>
          <link rel="self" href="https://atom.example/feed.atom"/>
          <link rel="alternate" type="text/html" href="https://atom.example/"/>
          <entry>
            <title>Release 2.0</title>
            <id>urn:uuid:1</id>
            <link rel="alternate" href="https://atom.example/r/2"/>
            <link rel="enclosure" href="https://atom.example/r/2.zip"/>
            <published>2026-10-04T10:00:00+02:00</published>
            <updated>2026-10-06T10:00:00Z</updated>
            <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Changelog</p></div></content>
          </entry>
          <entry>
            <title>Only a summary</title>
            <id>urn:uuid:2</id>
            <link href="https://atom.example/r/3"/>
            <updated>2026-10-05T10:00:00Z</updated>
            <summary type="html">&lt;p&gt;Summary text&lt;/p&gt;</summary>
          </entry>
        </feed>
        """;

    [Fact]
    public void Reads_Atom_with_alternate_links_published_before_updated_and_xhtml_content()
    {
        Assert.True(FeedParser.TryParse(Atom, new Uri("https://atom.example/feed.atom"), out var feed));

        Assert.Equal("Atom & Friends", feed.Title);
        Assert.Equal("https://atom.example/", feed.SiteUrl);
        var release = feed.Entries.Single(e => e.Guid == "urn:uuid:1");
        Assert.Equal("https://atom.example/r/2", release.Url);
        Assert.Equal(new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc), release.PublishedAt);
        Assert.Contains("<p>Changelog</p>", release.Html, StringComparison.Ordinal);
        var summary = feed.Entries.Single(e => e.Guid == "urn:uuid:2");
        Assert.Equal("https://atom.example/r/3", summary.Url);
        Assert.Equal("<p>Summary text</p>", summary.Html);
        Assert.Equal(new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc), summary.PublishedAt);
    }

    private const string JsonFeed =
        """
        {
          "version": "https://jsonfeed.org/version/1.1",
          "title": "JSON Blog",
          "home_page_url": "https://json.example/",
          "items": [
            {"id": "1", "url": "https://json.example/1", "title": "With html", "content_html": "<p>Hello</p>", "date_published": "2026-10-06T07:00:00Z"},
            {"id": "2", "url": "https://json.example/2", "title": "With text", "content_text": "a < b & c", "date_modified": "2026-10-05T07:00:00Z"},
            {"id": "3", "external_url": "https://elsewhere.example/3", "title": "Link post", "summary": "teaser"},
            {"id": "4", "title": "No url"}
          ]
        }
        """;

    [Fact]
    public void Reads_JSON_Feed_with_html_text_summary_and_external_urls()
    {
        Assert.True(FeedParser.TryParse(JsonFeed, new Uri("https://json.example/feed.json"), out var feed));

        Assert.Equal("JSON Blog", feed.Title);
        Assert.Equal("https://json.example/", feed.SiteUrl);
        Assert.Equal(3, feed.Entries.Count);
        Assert.Equal("<p>Hello</p>", feed.Entries.Single(e => e.Guid == "1").Html);
        Assert.Equal("a &lt; b &amp; c", feed.Entries.Single(e => e.Guid == "2").Html);   // plain text is escaped into html
        Assert.Equal(new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc), feed.Entries.Single(e => e.Guid == "2").PublishedAt);
        var link = feed.Entries.Single(e => e.Guid == "3");
        Assert.Equal("https://elsewhere.example/3", link.Url);
        Assert.Equal("teaser", link.Html);
        Assert.Null(link.PublishedAt);
    }

    [Theory]
    [InlineData("<rss version=\"2.0\"><channel><title>x</title><item>")]
    [InlineData("<rss><channel><title>x</title></channel>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    [InlineData("<html><head><title>A page</title></head><body>Hi</body></html>")]
    [InlineData("<rss version=\"2.0\"></rss>")]
    [InlineData("""{"title": "not a json feed", "items": []}""")]
    [InlineData("{ broken json")]
    [InlineData("[1, 2, 3]")]
    public void Anything_else_and_malformed_input_is_not_a_feed(string body) => Assert.False(FeedParser.TryParse(body, Url, out _));

    [Fact]
    public void A_doctype_is_ignored_and_never_expanded_or_fetched()
    {
        const string Hostile =
            """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <rss version="2.0"><channel><title>Safe</title><item><title>Item</title><link>https://x.example/1</link></item></channel></rss>
            """;

        Assert.True(FeedParser.TryParse(Hostile, Url, out var feed));
        Assert.Equal("Safe", feed.Title);
        Assert.Single(feed.Entries);
    }

    [Fact]
    public void A_byte_order_mark_and_control_characters_do_not_break_the_feed()
    {
        var body = "﻿  <rss version=\"2.0\"><channel><title>T</title><item><title>A\u0003B</title><link>https://x.example/1</link></item></channel></rss>";

        Assert.True(FeedParser.TryParse(body, Url, out var feed));
        Assert.Single(feed.Entries);
    }

    [Fact]
    public void Only_the_newest_200_entries_are_kept()
    {
        var items = string.Concat(Enumerable.Range(0, 250).Select(i =>
            $"<item><title>T{i}</title><link>https://x.example/{i}</link><pubDate>{new DateTime(2026, 1, 1).AddDays(i):R}</pubDate></item>"));
        var body = $"<rss version=\"2.0\"><channel><title>Big</title>{items}</channel></rss>";

        FeedParser.TryParse(body, Url, out var feed);

        Assert.Equal(FeedParser.MaxEntries, feed.Entries.Count);
        Assert.Equal("T249", feed.Entries[0].Title);
        Assert.Equal("T50", feed.Entries[^1].Title);
    }

    [Theory]
    [InlineData("Tue, 06 Oct 2026 09:30:00 GMT", 2026, 10, 6, 9, 30)]
    [InlineData("Tue, 06 Oct 2026 09:30:00 +0200", 2026, 10, 6, 7, 30)]
    [InlineData("Mon, 06 Oct 2026 09:30:00 PDT", 2026, 10, 6, 16, 30)]   // wrong weekday and a zone name, both common in the wild
    [InlineData("2026-10-06T09:30:00Z", 2026, 10, 6, 9, 30)]
    [InlineData("2026-10-06T09:30:00-05:00", 2026, 10, 6, 14, 30)]
    [InlineData("2026-10-06 09:30:00", 2026, 10, 6, 9, 30)]
    public void Reads_RFC_822_and_ISO_dates(string text, int y, int mo, int d, int h, int mi) =>
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc), FeedParser.Date(text));

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    public void An_unreadable_date_is_null(string text) => Assert.Null(FeedParser.Date(text));
}
