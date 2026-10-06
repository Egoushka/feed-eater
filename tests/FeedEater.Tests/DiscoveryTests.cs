using System.Net;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Fetch;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class FeedDiscoveryParsingTests
{
    private static readonly Uri Page = new("https://blog.example/about/");

    [Theory]
    [InlineData("""<link rel="alternate" type="application/rss+xml" href="/feed.xml">""", "https://blog.example/feed.xml")]
    [InlineData("""<link href="https://feeds.example/atom" type="application/atom+xml" rel="alternate" title="x">""", "https://feeds.example/atom")]
    [InlineData("""<LINK REL='alternate' TYPE='application/rss+xml' HREF='rss'>""", "https://blog.example/about/rss")]
    [InlineData("""<link rel="alternate" type="application/feed+json" href="/feed.json">""", "https://blog.example/feed.json")]
    [InlineData("""<link rel="stylesheet" href="/a.css"><link rel="alternate" type="application/rss+xml" href="/first.xml"><link rel="alternate" type="application/atom+xml" href="/second.xml">""", "https://blog.example/first.xml")]
    [InlineData("""<link rel="alternate" type="application/rss+xml" href="/a?x=1&amp;y=2">""", "https://blog.example/a?x=1&y=2")]
    public void Finds_the_feed_link_in_any_attribute_order_and_resolves_it(string html, string expected) =>
        Assert.Equal(expected, FeedDiscoverer.FindFeed(html, Page));

    [Theory]
    [InlineData("""<link rel="alternate" type="text/html" href="/de/">""")]
    [InlineData("""<link rel="alternate" type="application/rss+xml" href="javascript:alert(1)">""")]
    [InlineData("""<link rel="alternate" type="application/rss+xml" href="ftp://x/feed">""")]
    [InlineData("""<link rel="alternate" type="application/rss+xml">""")]
    [InlineData("""<link rel="stylesheet" type="application/rss+xml" href="/x">""")]
    [InlineData("<p>no head at all</p>")]
    public void Finds_nothing_for_other_links_or_unsafe_targets(string html) => Assert.Null(FeedDiscoverer.FindFeed(html, Page));

    private static LikedLink Like(long id, string url, string? link = null) => new(id, $"Post {id}", url, link);

    [Fact]
    public void Ranks_domains_by_likes_prefers_the_linked_site_and_drops_subscribed_social_and_blocked_ones()
    {
        var liked = new[]
        {
            Like(1, "https://news.ycombinator.com/item?id=1", "https://tool.dev/post"),
            Like(2, "https://www.reddit.com/r/x/comments/2", "https://www.tool.dev/other"),
            Like(3, "https://blog.once.example/a"),
            Like(4, "https://followed.example/a"),
            Like(5, "https://sub.followed2.example/a"),
            Like(6, "https://github.com/o/r/releases/tag/v1"),
            Like(7, "https://facebook.com/x"),
            Like(8, "https://news.ycombinator.com/item?id=8"),         // no external link: HN itself is skipped
            Like(9, "https://twice.example/1"),
            Like(10, "https://twice.example/2"),
            Like(10, "https://twice.example/2"),                          // the same item twice counts once
        };

        var ranked = FeedDiscoverer.Rank(liked, ["followed.example", "followed2.example"], ["facebook.com"]);

        Assert.Equal([("tool.dev", 2), ("twice.example", 2), ("blog.once.example", 1)], ranked.Select(r => (r.Domain, r.Why.Count)));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class FeedDiscovererTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (FeedDiscoverer Discoverer, StubHandler Pages, FakeTimeProvider Time) Build(Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        var time = new FakeTimeProvider(Now);
        var options = Options.Create(new FeedEaterOptions());
        var pages = new StubHandler((r, _) => answer?.Invoke(r) ?? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"""<html><head><link rel="alternate" type="application/atom+xml" href="https://{r.RequestUri!.Host}/atom.xml"></head></html>""", System.Text.Encoding.UTF8, "text/html"),
        });
        var fetcher = new SafeFetcher(new HttpClient(pages), options, new CursorStore(pg.Db), time, NullLogger<SafeFetcher>.Instance);
        return (new FeedDiscoverer(new DiscoveryStore(pg.Db), new ItemStore(pg.Db), fetcher, options, time, NullLogger<FeedDiscoverer>.Instance), pages, time);
    }

    private async Task LikeAsync(string host, int count = 1, string? link = null)
    {
        for (var i = 0; i < count; i++)
        {
            var id = await Seed.ItemAsync(pg, 1, $"Liked on {host} {i}", TestVectors.OneHot(1), DateTime.UtcNow);
            await using var c = await pg.Db.DataSource.OpenConnectionAsync();
            await c.ExecuteAsync("update items set url = @url, link_url = @link where id = @id", new { id, url = link is null ? $"https://{host}/p{i}" : $"https://www.reddit.com/r/x/comments/{id}", link = link is null ? null : $"https://{host}/p{i}" });
            await new FeedbackStore(pg.Db).SetVoteAsync(id, 1, default);
        }
    }

    [Fact]
    public async Task Finds_feed_urls_for_the_top_domains_three_at_a_time_and_never_rechecks_inside_a_month()
    {
        await LikeAsync("a.example", 5);
        await LikeAsync("b.example", 4, link: "x");
        await LikeAsync("c.example", 3);
        await LikeAsync("d.example", 2);
        var (discoverer, pages, time) = Build();

        Assert.Equal(3, await discoverer.RunAsync(default));
        Assert.Equal(3, pages.Calls.Count);
        var first = await discoverer.SuggestionsAsync(default);

        Assert.Equal(["a.example", "b.example", "c.example", "d.example"], first.Select(s => s.Domain));
        Assert.Equal(["https://a.example/atom.xml", "https://b.example/atom.xml", "https://c.example/atom.xml", null], first.Select(s => s.Found?.FeedUrl));
        Assert.Equal(5, first[0].Why.Count);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await discoverer.RunAsync(default));   // only d.example is left
        Assert.Equal(0, await discoverer.RunAsync(default));
        Assert.Equal(4, pages.Calls.Count);

        time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(3, await discoverer.RunAsync(default));   // a month later the top ones are checked again
    }

    [Fact]
    public async Task Records_a_homepage_without_a_feed_or_that_failed_and_does_not_retry_them()
    {
        await LikeAsync("plain.example", 3);
        await LikeAsync("broken.example", 2);
        var (discoverer, pages, _) = Build(r => r.RequestUri!.Host == "plain.example"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><head></head></html>", System.Text.Encoding.UTF8, "text/html") }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await discoverer.RunAsync(default);
        await discoverer.RunAsync(default);

        var s = await discoverer.SuggestionsAsync(default);
        Assert.Equal(("none", null), (s[0].Found!.Status, s[0].Found!.FeedUrl));
        Assert.Equal("failed", s[1].Found!.Status);
        Assert.Equal(2, pages.Calls.Count);
    }

    [Fact]
    public async Task Skips_subscribed_sites_and_never_fetches_a_blocked_or_internal_host()
    {
        await LikeAsync("followed.example", 3);
        await LikeAsync("facebook.com", 3);
        await LikeAsync("fine.example", 1);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update feeds set site_url = 'https://www.followed.example/' where id = 1");
            await c.ExecuteAsync("insert into feeds (id, title, site_url) values (2, 'Other', 'https://other.example/')");
        }

        var (discoverer, pages, _) = Build();
        await discoverer.RunAsync(default);

        Assert.Equal(["fine.example"], (await discoverer.SuggestionsAsync(default)).Select(s => s.Domain));
        Assert.Equal(["fine.example"], pages.Calls.Select(c => new Uri(c.Uri).Host));
    }

    [Fact]
    public async Task Old_votes_do_not_count()
    {
        await LikeAsync("ancient.example", 2);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update votes set at = now() - interval '200 days'");
        }

        Assert.Empty(await Build().Discoverer.SuggestionsAsync(default));
    }
}
