using System.Xml;
using Dapper;
using FeedEater.Sources;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class OpmlParseTests
{
    private const string Subscriptions =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <opml version="2.0">
          <head><title>Subscriptions</title></head>
          <body>
            <outline text="Loose" title="Loose" type="rss" xmlUrl="https://loose.example/feed" htmlUrl="https://loose.example/"/>
            <outline text="Tech &amp; Dev" title="Tech &amp; Dev">
              <outline text="Blog A" type="rss" xmlUrl="https://a.example/rss"/>
              <outline text="Blog B" type="rss" xmlUrl="https://b.example/atom.xml" htmlUrl="https://b.example/"/>
            </outline>
            <outline text="News">
              <outline text="Nested">
                <outline text="Deep" xmlUrl="https://deep.example/feed"/>
              </outline>
            </outline>
            <outline text="Not http" xmlUrl="ftp://x.example/feed"/>
            <outline text="No url" type="link" url="https://x.example/"/>
            <outline xmlUrl="https://nameless.example/feed"/>
          </body>
        </opml>
        """;

    [Fact]
    public void Folders_become_categories_and_only_http_feeds_are_kept()
    {
        var feeds = Opml.Parse(Subscriptions);

        Assert.Equal(
            [
                ("Loose", "https://loose.example/feed", "https://loose.example/", (string?)null),
                ("Blog A", "https://a.example/rss", null, "Tech & Dev"),
                ("Blog B", "https://b.example/atom.xml", "https://b.example/", "Tech & Dev"),
                ("Deep", "https://deep.example/feed", null, "Nested"),
                ("https://nameless.example/feed", "https://nameless.example/feed", null, null),
            ],
            feeds.Select(f => (f.Title, f.FeedUrl, f.SiteUrl, f.Category)));
    }

    [Theory]
    [InlineData("<opml><body><outline xmlUrl=")]
    [InlineData("not opml")]
    [InlineData("")]
    public void Malformed_xml_throws(string text) => Assert.ThrowsAny<XmlException>(() => Opml.Parse(text));

    [Fact]
    public void A_file_without_a_body_has_no_feeds() => Assert.Empty(Opml.Parse("<opml version=\"2.0\"><head/></opml>"));

    [Fact]
    public void A_doctype_is_ignored()
    {
        const string Hostile = "<?xml version=\"1.0\"?><!DOCTYPE opml [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><opml><body><outline text=\"A\" xmlUrl=\"https://a.example/f\"/></body></opml>";

        Assert.Single(Opml.Parse(Hostile));
    }

    [Fact]
    public void Writing_then_parsing_gives_the_same_feeds_with_odd_characters_intact()
    {
        var feeds = new[]
        {
            new Feed(1, "Plain", null, "https://plain.example/", FeedUrl: "https://plain.example/feed?a=1&b=2"),
            new Feed(2, "Tom & \"Jerry\" <3", "Cartoons & Co", null, FeedUrl: "https://tj.example/rss"),
            new Feed(3, "Другий", "Cartoons & Co", null, FeedUrl: "https://uk.example/rss"),
            new Feed(4, "No url (Miniflux gone)", null, null),
        };

        var xml = Opml.Write(feeds);
        var back = Opml.Parse(xml);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.Ordinal);
        Assert.Equal(
            [
                ("Plain", "https://plain.example/feed?a=1&b=2", "https://plain.example/", (string?)null),
                ("Tom & \"Jerry\" <3", "https://tj.example/rss", null, "Cartoons & Co"),
                ("Другий", "https://uk.example/rss", null, "Cartoons & Co"),
            ],
            back.Select(f => (f.Title, f.FeedUrl, f.SiteUrl, f.Category)).OrderBy(f => f.Item4 ?? "").ThenBy(f => f.Item1, StringComparer.Ordinal));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class OpmlRoundTripTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Import_stores_feeds_with_folders_as_categories_and_export_reproduces_them()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(System.Net.HttpStatusCode.NotFound));
        const string Opml =
            """
            <opml version="2.0"><body>
              <outline text="Loose" xmlUrl="https://loose.example/feed"/>
              <outline text="Tech"><outline text="Blog A" xmlUrl="https://a.example/rss" htmlUrl="https://a.example/"/><outline text="Blog B" xmlUrl="https://b.example/rss"/></outline>
            </body></opml>
            """;

        var imported = await kit.Manager.ImportAsync(Opml, default);
        var exported = await kit.Manager.ExportAsync(default);

        Assert.Equal(new ImportResult(null, 3, 0), imported);
        Assert.Equal(
            [("Blog A", "Tech"), ("Blog B", "Tech"), ("Loose", null)],
            (await kit.Feeds.ListAsync(default)).Select(f => (f.Title, f.Category)).OrderBy(f => f.Title, StringComparer.Ordinal));
        Assert.Empty(kit.Stub.Calls);   // nothing is fetched at import; the ingest loop picks the feeds up

        // The export imports again into a fresh database with the same feeds, and is a no-op into this one.
        Assert.Equal(new ImportResult(null, 0, 3), await kit.Manager.ImportAsync(exported, default));
        await pg.ResetAsync();
        Assert.Equal(new ImportResult(null, 3, 0), await kit.Manager.ImportAsync(exported, default));
        Assert.Equal(
            [("Blog A", "Tech", "https://a.example/rss"), ("Blog B", "Tech", "https://b.example/rss"), ("Loose", null, "https://loose.example/feed")],
            (await kit.Feeds.ListAsync(default)).Select(f => (f.Title, f.Category, f.FeedUrl)).OrderBy(f => f.Title, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Importing_the_same_file_twice_adds_nothing_the_second_time_even_when_the_url_differs_in_case()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(System.Net.HttpStatusCode.NotFound));

        await kit.Manager.ImportAsync("<opml><body><outline text=\"A\" xmlUrl=\"https://a.example/Feed\"/></body></opml>", default);
        var again = await kit.Manager.ImportAsync("<opml><body><outline text=\"A\" xmlUrl=\"https://A.example/feed\"/></body></opml>", default);

        Assert.Equal(new ImportResult(null, 0, 1), again);
    }

    [Fact]
    public async Task A_file_that_is_not_OPML_is_reported_and_stores_nothing()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(System.Net.HttpStatusCode.NotFound));

        var result = await kit.Manager.ImportAsync("<html>nope", default);

        Assert.False(result.Ok);
        Assert.Empty(await kit.Feeds.ListAsync(default));
    }

    [Fact]
    public async Task New_feeds_take_ids_above_a_billion_and_miniflux_ids_are_untouched()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Status(System.Net.HttpStatusCode.NotFound));
        await new ItemStore(pg.Db).UpsertFeedAsync(new Feed(42, "From Miniflux", null, null, FeedUrl: "https://mf.example/feed"), default);

        await kit.Manager.ImportAsync("<opml><body><outline text=\"A\" xmlUrl=\"https://a.example/rss\"/><outline text=\"B\" xmlUrl=\"https://b.example/rss\"/></body></opml>", default);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var ids = (await c.QueryAsync<long>("select id from feeds order by id")).ToList();
        Assert.Equal(42, ids[0]);
        Assert.All(ids.Skip(1), id => Assert.True(id > 1_000_000_000));
        Assert.Equal(3, ids.Count);
    }
}
