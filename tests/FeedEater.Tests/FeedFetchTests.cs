using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Fetch;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedFetchTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private const string Xml = "<rss version=\"2.0\"><channel><title>t</title></channel></rss>";

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpResponseMessage Ok(string body = Xml, string? etag = null, DateTimeOffset? modified = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/rss+xml") };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        response.Content.Headers.LastModified = modified;
        return response;
    }

    private (SafeFetcher Fetcher, StubHandler Stub, FakeTimeProvider Time) Build(Func<HttpRequestMessage, HttpResponseMessage> answer, Action<FeedEaterOptions>? tweak = null)
    {
        var time = new FakeTimeProvider(Now);
        var stub = new StubHandler((request, _) => answer(request));
        var settings = new FeedEaterOptions();
        tweak?.Invoke(settings);
        return (new SafeFetcher(new HttpClient(stub), Options.Create(settings), new CursorStore(pg.Db), time, NullLogger<SafeFetcher>.Instance), stub, time);
    }

    [Fact]
    public async Task The_validators_are_sent_and_a_304_answers_not_modified()
    {
        HttpRequestMessage? seen = null;
        var (fetcher, _, _) = Build(r =>
        {
            seen = r;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        var result = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions("\"v1\"", "Tue, 06 Oct 2026 10:00:00 GMT"), default);

        Assert.Equal(FetchOutcome.NotModified, result.Outcome);
        Assert.Null(result.Body);
        Assert.Equal("\"v1\"", Assert.Single(seen!.Headers.GetValues("If-None-Match")));
        Assert.Equal("Tue, 06 Oct 2026 10:00:00 GMT", Assert.Single(seen.Headers.GetValues("If-Modified-Since")));
    }

    [Fact]
    public async Task A_changed_feed_returns_the_body_and_the_new_validators()
    {
        var modified = new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero);
        var (fetcher, _, _) = Build(_ => Ok(etag: "\"v2\"", modified: modified));

        var result = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions("\"v1\""), default);

        Assert.True(result.Ok);
        Assert.Equal(Xml, result.Body);
        Assert.Equal("\"v2\"", result.ETag);
        Assert.Equal("Tue, 06 Oct 2026 11:00:00 GMT", result.LastModified);
    }

    [Fact]
    public async Task A_first_fetch_sends_no_validators()
    {
        HttpRequestMessage? seen = null;
        var (fetcher, _, _) = Build(r =>
        {
            seen = r;
            return Ok();
        });

        await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);

        Assert.False(seen!.Headers.Contains("If-None-Match"));
        Assert.False(seen.Headers.Contains("If-Modified-Since"));
        Assert.Contains("rss+xml", seen.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_followed_and_reported()
    {
        var (fetcher, stub, _) = Build(r => r.RequestUri!.Host == "old.example"
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://new.example/feed.xml") } }
            : Ok());

        var result = await fetcher.FetchFeedAsync(new Uri("https://old.example/feed"), new FeedConditions(), default);

        Assert.True(result.Ok);
        Assert.Equal("https://new.example/feed.xml", result.FinalUri!.AbsoluteUri);
        Assert.Equal(2, stub.Calls.Count);
    }

    [Fact]
    public async Task A_404_fails_the_feed_without_parking_the_host_for_pages()
    {
        var (fetcher, stub, time) = Build(r => r.RequestUri!.AbsolutePath == "/feed" ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<p>page</p>", System.Text.Encoding.UTF8, "text/html"),
        });

        var feed = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);
        time.Advance(TimeSpan.FromSeconds(2));
        var again = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);
        time.Advance(TimeSpan.FromSeconds(2));
        var page = await fetcher.FetchAsync(new Uri("https://example.com/post"), default);

        Assert.Equal(FetchOutcome.Failed, feed.Outcome);
        Assert.Equal("404", feed.Detail);
        Assert.Equal(FetchOutcome.Failed, again.Outcome);
        Assert.True(page.Ok);
        Assert.Equal(3, stub.Calls.Count);
    }

    [Fact]
    public async Task A_page_failure_still_parks_the_host_for_pages()
    {
        var (fetcher, _, time) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await fetcher.FetchAsync(new Uri("https://example.com/post"), default);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(FetchOutcome.Cached, (await fetcher.FetchAsync(new Uri("https://example.com/other"), default)).Outcome);
    }

    [Fact]
    public async Task A_body_over_5_MB_is_refused_and_one_of_exactly_5_MB_is_read()
    {
        const int Cap = 5 * 1024 * 1024;
        var (fetcher, _, time) = Build(r => Ok(new string('a', r.RequestUri!.AbsolutePath == "/huge" ? Cap + 1 : Cap)));

        var huge = await fetcher.FetchFeedAsync(new Uri("https://example.com/huge"), new FeedConditions(), default);
        time.Advance(TimeSpan.FromSeconds(2));
        var edge = await fetcher.FetchFeedAsync(new Uri("https://example.com/edge"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.TooLarge, huge.Outcome);
        Assert.Null(huge.Body);
        Assert.True(edge.Ok);
        Assert.Equal(Cap, edge.Body!.Length);
    }

    [Theory]
    [InlineData("http://10.0.0.5/feed.xml")]
    [InlineData("http://127.0.0.1/feed.xml")]
    [InlineData("http://100.64.0.2/feed.xml")]
    [InlineData("http://[::1]/feed.xml")]
    [InlineData("https://example.com:8443/feed.xml")]
    public async Task A_private_host_or_odd_port_is_refused_before_any_request(string url)
    {
        var (fetcher, stub, _) = Build(_ => Ok());

        var result = await fetcher.FetchFeedAsync(new Uri(url), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Refused, result.Outcome);
        Assert.Empty(stub.Calls);
    }

    [Theory]
    [InlineData("http://10.0.0.5/feed.xml")]
    [InlineData("http://10.0.0.5:1200/feed.xml")]
    [InlineData("http://rsshub.internal:1200/feed.xml")]
    public async Task A_host_in_AllowedHosts_may_be_private_and_use_any_port(string url)
    {
        var (fetcher, stub, _) = Build(_ => Ok(), o => o.Source.AllowedHosts = ["10.0.0.5", "RSSHub.internal"]);

        var result = await fetcher.FetchFeedAsync(new Uri(url), new FeedConditions(), default);

        Assert.True(result.Ok);
        Assert.Single(stub.Calls);
    }

    [Fact]
    public async Task AllowedHosts_names_only_the_listed_host()
    {
        var (fetcher, stub, _) = Build(_ => Ok(), o => o.Source.AllowedHosts = ["10.0.0.5"]);

        var result = await fetcher.FetchFeedAsync(new Uri("http://10.0.0.6/feed.xml"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Refused, result.Outcome);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task A_redirect_from_a_feed_to_a_private_host_is_refused_unless_it_is_allowed()
    {
        var redirect = new Func<HttpRequestMessage, HttpResponseMessage>(r => r.RequestUri!.Host == "public.example"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://10.0.0.5/secret") } }
            : Ok("internal"));
        var (refusing, refusedCalls, _) = Build(redirect);
        var (allowing, allowedCalls, _) = Build(redirect, o => o.Source.AllowedHosts = ["10.0.0.5"]);

        var refused = await refusing.FetchFeedAsync(new Uri("https://public.example/feed"), new FeedConditions(), default);
        var allowed = await allowing.FetchFeedAsync(new Uri("https://public.example/feed"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Refused, refused.Outcome);
        Assert.Single(refusedCalls.Calls);
        Assert.True(allowed.Ok);
        Assert.Equal(2, allowedCalls.Calls.Count);
    }

    [Fact]
    public async Task Feeds_are_not_held_to_the_blocked_list_or_the_daily_page_cap()
    {
        var (fetcher, stub, _) = Build(_ => Ok(), o => o.Fetch.MaxPerDay = 0);

        var feed = await fetcher.FetchFeedAsync(new Uri("https://www.youtube.com/feeds/videos.xml?channel_id=x"), new FeedConditions(), default);
        var page = await fetcher.FetchAsync(new Uri("https://example.com/post"), default);

        Assert.True(feed.Ok);
        Assert.Equal(FetchOutcome.CapReached, page.Outcome);
        Assert.Single(stub.Calls);
    }

    [Fact]
    public async Task A_corrupt_compressed_body_is_a_failed_fetch_not_an_exception()
    {
        var (fetcher, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new System.IO.Compression.GZipStream(new MemoryStream("not gzip"u8.ToArray()), System.IO.Compression.CompressionMode.Decompress)),
        });

        var result = await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Equal(nameof(InvalidDataException), result.Detail);
    }

    [Fact]
    public async Task A_feed_of_any_content_type_is_returned()
    {
        var (fetcher, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml, System.Text.Encoding.UTF8, "application/octet-stream") });

        Assert.True((await fetcher.FetchFeedAsync(new Uri("https://example.com/feed"), new FeedConditions(), default)).Ok);
    }
}

public sealed class TrustedHostConnectorTests
{
    private static (GuardedConnector Connector, List<(IPAddress Address, int Port)> Connects) Build(string answer)
    {
        var connects = new List<(IPAddress, int)>();
        var connector = new GuardedConnector(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(answer) }),
            (address, port, _) =>
            {
                connects.Add((address, port));
                return Task.FromResult<Stream>(new MemoryStream());
            });
        return (connector, connects);
    }

    [Fact]
    public async Task A_trusted_host_may_resolve_to_a_private_address_on_any_port()
    {
        var (connector, connects) = Build("10.0.0.5");

        await connector.Trusting(["rsshub.internal"]).ConnectAsync("RSSHub.internal", 1200, default);

        Assert.Equal([(IPAddress.Parse("10.0.0.5"), 1200)], connects);
    }

    [Fact]
    public async Task Other_hosts_stay_refused_by_a_trusting_connector()
    {
        var (connector, connects) = Build("10.0.0.5");
        var trusting = connector.Trusting(["rsshub.internal"]);

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await trusting.ConnectAsync("other.example", 443, default));
        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await trusting.ConnectAsync("other.example", 1200, default));

        Assert.Empty(connects);
    }

    [Fact]
    public async Task The_default_connector_trusts_nobody()
    {
        var (connector, connects) = Build("10.0.0.5");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("rsshub.internal", 443, default));

        Assert.Empty(connects);
    }
}
