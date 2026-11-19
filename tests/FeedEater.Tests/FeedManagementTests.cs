using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Loops;
using FeedEater.Issues;
using FeedEater.Sources;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedManagementTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SourceKit.Now;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Atom =
        """<feed xmlns="http://www.w3.org/2005/Atom"><title>Atom feed</title><link href="https://atom.example/"/><entry><title>Entry</title><id>e1</id><link href="https://atom.example/e1"/><updated>2026-10-06T10:00:00Z</updated><summary>s</summary></entry></feed>""";

    private const string Json =
        """{"version":"https://jsonfeed.org/version/1.1","title":"JSON feed","home_page_url":"https://json.example/","items":[{"id":"1","url":"https://json.example/1","title":"Entry","content_text":"t","date_published":"2026-10-06T10:00:00Z"}]}""";

    [Theory]
    [InlineData("rss", "Feed title")]
    [InlineData("atom", "Atom feed")]
    [InlineData("json", "JSON feed")]
    public async Task Adding_a_feed_url_subscribes_and_stores_its_recent_entries_in_each_format(string format, string title)
    {
        var body = format switch
        {
            "atom" => Atom,
            "json" => Json,
            _ => SourceKit.Rss(("g1", "Entry", Now.AddHours(-2), "t")),
        };
        var kit = new SourceKit(pg, _ => SourceKit.Xml(body, format == "json" ? "application/feed+json" : "application/xml"));

        var result = await kit.Manager.AddAsync("https://x.example/feed", default);

        Assert.True(result.Ok);
        Assert.False(result.Existing);
        Assert.Equal(1, result.Items);
        Assert.Equal(["Entry"], (await new ItemStore(pg.Db).SearchAsync(null, "Entry", null, null, null, null, null, 10, default)).Select(h => h.Title));
        var feed = Assert.Single(await kit.Feeds.ListAsync(default));
        Assert.Equal(title, feed.Title);
        Assert.Equal("https://x.example/feed", feed.FeedUrl);
        Assert.Equal(Now.UtcDateTime, feed.LastFetchedAt);
    }

    [Fact]
    public async Task A_site_address_is_resolved_to_the_feed_its_page_links()
    {
        // The feed lives on another host, so the one-request-a-second wait per host does not apply between the two fetches.
        var kit = new SourceKit(pg, r => r.RequestUri!.Host == "feeds.example"
            ? SourceKit.Xml(SourceKit.Rss(("g1", "Entry", Now.AddHours(-1), "t")))
            : SourceKit.Xml("""<html><head><link rel="alternate" type="application/rss+xml" href="https://feeds.example/feed.xml"></head><body>hi</body></html>""", "text/html"));

        var result = await kit.Manager.AddAsync("blog.example", default);   // no scheme

        Assert.True(result.Ok);
        Assert.Equal("https://blog.example/", kit.Stub.Calls[0].Uri);
        Assert.Equal("https://feeds.example/feed.xml", Assert.Single(await kit.Feeds.ListAsync(default)).FeedUrl);
    }

    [Fact]
    public async Task Adding_a_feed_twice_says_so_and_keeps_one()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(SourceKit.Rss(("g1", "Entry", Now.AddHours(-1), "t"))));
        await kit.Manager.AddAsync("https://x.example/feed", default);
        kit.Time.Advance(TimeSpan.FromSeconds(2));

        var again = await kit.Manager.AddAsync("https://X.example/feed", default);

        Assert.True(again.Ok);
        Assert.True(again.Existing);
        Assert.Single(await kit.Feeds.ListAsync(default));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x.example/feed")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Something_that_is_not_a_web_address_is_refused_without_a_request(string input)
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(Atom));

        var result = await kit.Manager.AddAsync(input, default);

        Assert.False(result.Ok);
        Assert.Empty(kit.Stub.Calls);
        Assert.Empty(await kit.Feeds.ListAsync(default));
    }

    [Fact]
    public async Task A_page_with_no_feed_link_and_an_unreachable_address_store_nothing_and_say_why()
    {
        var page = new SourceKit(pg, _ => SourceKit.Xml("<html><body>no feed here</body></html>", "text/html"));
        var gone = new SourceKit(pg, _ => SourceKit.Status(HttpStatusCode.NotFound));

        var noFeed = await page.Manager.AddAsync("https://x.example/", default);
        var missing = await gone.Manager.AddAsync("https://y.example/feed", default);

        Assert.Equal("No feed found at that address.", noFeed.Error);
        Assert.Equal("Could not read it: HTTP 404.", missing.Error);
        Assert.Empty(await page.Feeds.ListAsync(default));
    }

    [Fact]
    public async Task A_private_address_is_not_added_unless_allowed()
    {
        var kit = new SourceKit(pg, _ => SourceKit.Xml(Atom));

        var result = await kit.Manager.AddAsync("http://10.0.0.5/feed", default);

        Assert.Contains("Source:AllowedHosts", result.Error, StringComparison.Ordinal);
        Assert.Empty(kit.Stub.Calls);
    }
}
