using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Fetch;
using FeedEater.Loops;
using FeedEater.Sources;
using FeedEater.Storage;

namespace FeedEater.Tests;

/// <summary>The built-in reader wired to a stubbed network and a fake clock; one per test. Feeds on different hosts avoid the one-a-second wait.</summary>
public sealed class SourceKit
{
    public static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public SourceKit(PostgresFixture pg, Func<HttpRequestMessage, HttpResponseMessage> answer, Action<FeedEaterOptions>? tweak = null)
    {
        Time = new FakeTimeProvider(Now);
        Stub = new StubHandler((request, _) => answer(request));
        var settings = new FeedEaterOptions();
        tweak?.Invoke(settings);
        Options = Microsoft.Extensions.Options.Options.Create(settings);
        Fetcher = new SafeFetcher(new HttpClient(Stub), Options, new CursorStore(pg.Db), Time, NullLogger<SafeFetcher>.Instance);
        Items = new ItemStore(pg.Db);
        Feeds = new FeedStore(pg.Db);
        Health = new LoopHealth(Time);
        var reader = new FeedReader(Fetcher);
        Poller = new FeedPoller(Feeds, reader, Items, Health, Options, Time, NullLogger<FeedPoller>.Instance);
        Manager = new FeedManager(Feeds, reader, Poller, Items);
    }

    public FakeTimeProvider Time { get; }
    public StubHandler Stub { get; }
    public IOptions<FeedEaterOptions> Options { get; }
    public SafeFetcher Fetcher { get; }
    public ItemStore Items { get; }
    public FeedStore Feeds { get; }
    public LoopHealth Health { get; }
    public FeedPoller Poller { get; }
    public FeedManager Manager { get; }

    public static HttpResponseMessage Xml(string body, string type = "application/rss+xml", string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    /// <summary>An RSS 2.0 feed; each entry is (guid, title, published or null, html).</summary>
    public static string Rss(params (string Guid, string Title, DateTimeOffset? Published, string Html)[] entries) =>
        "<rss version=\"2.0\"><channel><title>Feed title</title><link>https://site.example/</link>"
        + string.Concat(entries.Select(e =>
            $"<item><title>{WebUtility.HtmlEncode(e.Title)}</title><link>https://site.example/{e.Guid}</link><guid isPermaLink=\"false\">{e.Guid}</guid>"
            + (e.Published is { } at ? $"<pubDate>{at:R}</pubDate>" : "")
            + $"<description>{WebUtility.HtmlEncode(e.Html)}</description></item>"))
        + "</channel></rss>";

    public async Task<FeedState> AddFeedAsync(string url, string title = "Feed")
    {
        var (id, _) = await Feeds.AddAsync(title, url, null, null, default);
        return (await Feeds.GetAsync(id, default))!;
    }
}
