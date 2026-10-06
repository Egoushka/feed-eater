using System.Net;
using System.Net.Sockets;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Fetch;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class IpGuardTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:81b::200e")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(IpGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.0")]
    [InlineData("100.64.0.2")]          // the tailnet address of this very box
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")]     // cloud metadata
    [InlineData("169.254.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]      // NAT64 of 10.0.0.1
    [InlineData("2002:a00:1::1")]       // 6to4 of 10.0.0.1
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]   // Teredo
    [InlineData("2001:db8::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("100::1")]
    public void Internal_and_special_addresses_are_refused(string ip) => Assert.False(IpGuard.IsPublic(IPAddress.Parse(ip)));
}

public sealed class GuardedConnectorTests
{
    private static (GuardedConnector Connector, List<(IPAddress Address, int Port)> Connects, int[] Resolves) Build(params string[] answers)
    {
        var connects = new List<(IPAddress, int)>();
        var resolves = new[] { 0 };
        var connector = new GuardedConnector(
            (_, _) =>
            {
                resolves[0]++;
                return Task.FromResult(answers.Select(IPAddress.Parse).ToArray());
            },
            (address, port, _) =>
            {
                connects.Add((address, port));
                return Task.FromResult<Stream>(new MemoryStream());
            });
        return (connector, connects, resolves);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("100.64.0.2")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public async Task A_name_that_resolves_to_an_internal_address_is_refused_without_connecting(string answer)
    {
        var (connector, connects, _) = Build(answer);

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("example.test", 443, default));

        Assert.Empty(connects);
    }

    [Fact]
    public async Task One_internal_answer_among_public_ones_refuses_the_whole_fetch()
    {
        var (connector, connects, _) = Build("8.8.8.8", "10.0.0.1");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("rebind.test", 443, default));

        Assert.Empty(connects);
    }

    [Fact]
    public async Task The_validated_address_is_the_one_connected_to_and_the_name_is_resolved_once()
    {
        var (connector, connects, resolves) = Build("8.8.4.4");

        await connector.ConnectAsync("example.test", 443, default);

        Assert.Equal(1, resolves[0]);
        Assert.Equal([(IPAddress.Parse("8.8.4.4"), 443)], connects);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Literal_internal_hosts_are_refused_without_a_lookup(string host)
    {
        var (connector, connects, resolves) = Build("8.8.8.8");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync(host.Trim('[', ']'), 80, default));

        Assert.Equal(0, resolves[0]);
        Assert.Empty(connects);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(8080)]
    [InlineData(5432)]
    public async Task Only_ports_80_and_443_are_allowed(int port)
    {
        var (connector, _, _) = Build("8.8.8.8");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("example.test", port, default));
    }

    [Fact]
    public async Task A_second_address_is_tried_when_the_first_does_not_connect()
    {
        var tried = new List<IPAddress>();
        var connector = new GuardedConnector(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4") }),
            (address, _, _) =>
            {
                tried.Add(address);
                return tried.Count == 1 ? throw new SocketException((int)SocketError.ConnectionRefused) : Task.FromResult<Stream>(new MemoryStream());
            });

        await connector.ConnectAsync("example.test", 443, default);

        Assert.Equal(2, tried.Count);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class SafeFetcherTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync()
    {
        SafeFetcher.Timeout = TimeSpan.FromSeconds(10);
        return Task.CompletedTask;
    }

    private static HttpResponseMessage Html(string body, string type = "text/html; charset=utf-8", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, type.Split(';')[0]) };

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private (SafeFetcher Fetcher, StubHandler Stub, FakeTimeProvider Time) Build(Func<HttpRequestMessage, HttpResponseMessage> answer, Action<FetchOptions>? tweak = null)
    {
        var time = new FakeTimeProvider(Now);
        var stub = new StubHandler((request, _) => answer(request));
        var settings = new FeedEaterOptions();
        tweak?.Invoke(settings.Fetch);
        return (new SafeFetcher(new HttpClient(stub), Options.Create(settings), new CursorStore(pg.Db), time, NullLogger<SafeFetcher>.Instance), stub, time);
    }

    [Fact]
    public async Task Fetches_a_page_with_no_cookies_no_credentials_and_a_clear_user_agent()
    {
        var (fetcher, stub, _) = Build(_ => Html("<p>hello</p>"));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/post"), default);

        Assert.True(result.Ok);
        Assert.Equal("<p>hello</p>", result.Body);
        var request = Assert.Single(stub.Calls);
        Assert.Equal("https://example.com/post", request.Uri);
    }

    [Fact]
    public async Task Requests_carry_only_the_user_agent_and_accept_headers()
    {
        HttpRequestMessage? seen = null;
        var (fetcher, _, _) = Build(r =>
        {
            seen = r;
            return Html("x");
        });

        await fetcher.FetchAsync(new Uri("https://example.com/"), default);

        Assert.Contains("feed-eater", seen!.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Null(seen.Headers.Authorization);
        Assert.False(seen.Headers.Contains("Cookie"));
        Assert.False(seen.Headers.Contains("Referer"));
        Assert.False(seen.Headers.Contains("X-Api-Key"));
    }

    [Theory]
    [InlineData("ftp://example.com/file", FetchOutcome.Refused)]
    [InlineData("file:///etc/passwd", FetchOutcome.Refused)]
    [InlineData("gopher://example.com/", FetchOutcome.Refused)]
    [InlineData("https://example.com:8443/", FetchOutcome.Refused)]
    [InlineData("http://example.com:22/", FetchOutcome.Refused)]
    [InlineData("https://user:pass@example.com/", FetchOutcome.Refused)]
    [InlineData("http://127.0.0.1/", FetchOutcome.Refused)]
    [InlineData("http://10.0.0.1/admin", FetchOutcome.Refused)]
    [InlineData("http://100.64.0.2:80/", FetchOutcome.Refused)]
    [InlineData("http://169.254.169.254/latest/meta-data/", FetchOutcome.Refused)]
    [InlineData("http://[::1]/", FetchOutcome.Refused)]
    [InlineData("http://[fd00::1]/", FetchOutcome.Refused)]
    [InlineData("https://facebook.com/x", FetchOutcome.Blocked)]
    [InlineData("https://m.facebook.com/x", FetchOutcome.Blocked)]
    [InlineData("https://www.youtube.com/watch?v=1", FetchOutcome.Blocked)]
    public async Task Refuses_before_any_request(string url, FetchOutcome expected)
    {
        var (fetcher, stub, _) = Build(_ => Html("secret"));

        var result = await fetcher.FetchAsync(new Uri(url), default);

        Assert.Equal(expected, result.Outcome);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task Does_not_block_a_host_that_only_ends_with_a_blocked_name()
    {
        var (fetcher, _, _) = Build(_ => Html("ok"));

        Assert.True((await fetcher.FetchAsync(new Uri("https://notfacebook.com/"), default)).Ok);
    }

    [Fact]
    public async Task Follows_up_to_three_redirects_and_then_gives_up()
    {
        var (three, _, time) = Build(r => r.RequestUri!.AbsolutePath switch
        {
            "/a" => Redirect("/b"),
            "/b" => Redirect("https://other.example/c"),
            "/c" => Redirect("/d"),
            _ => Html("done"),
        });
        var task = three.FetchAsync(new Uri("https://example.com/a"), default);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        var ok = await task;
        Assert.True(ok.Ok);
        Assert.Equal("https://other.example/d", ok.FinalUri!.ToString());

        var (loop, stub, time2) = Build(_ => Redirect("/again"));
        var looping = loop.FetchAsync(new Uri("https://example.com/start"), default);
        while (!looping.IsCompleted)
        {
            time2.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        Assert.Equal(FetchOutcome.TooManyRedirects, (await looping).Outcome);
        Assert.Equal(4, stub.Calls.Count);   // the start and three redirects
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://example.com:8443/x")]
    [InlineData("https://facebook.com/login")]
    public async Task A_redirect_to_a_refused_target_is_refused_and_not_followed(string target)
    {
        var (fetcher, stub, _) = Build(r => r.RequestUri!.Host == "example.com" ? Redirect(target) : Html("internal secret"));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/start"), default);

        Assert.True(result.Outcome is FetchOutcome.Refused or FetchOutcome.Blocked);
        Assert.Null(result.Body);
        Assert.Single(stub.Calls);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    [InlineData("application/json")]
    public async Task Only_html_and_plain_text_are_read(string type)
    {
        var (fetcher, _, _) = Build(_ => Html("bytes", type));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/file"), default);

        Assert.Equal(FetchOutcome.BadType, result.Outcome);
        Assert.Null(result.Body);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xhtml+xml")]
    public async Task Plain_text_and_xhtml_are_read(string type) =>
        Assert.True((await Build(_ => Html("fine", type)).Fetcher.FetchAsync(new Uri("https://example.com/"), default)).Ok);

    [Fact]
    public async Task The_body_is_cut_at_one_megabyte()
    {
        var (fetcher, _, _) = Build(_ => Html(new string('a', 3 * 1024 * 1024)));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/big"), default);

        Assert.True(result.Ok);
        Assert.Equal(1024 * 1024, result.Body!.Length);
    }

    [Fact]
    public async Task A_failing_host_is_remembered_for_a_day_and_not_retried()
    {
        var (fetcher, stub, time) = Build(_ => Html("oops", status: HttpStatusCode.InternalServerError));

        Assert.Equal(FetchOutcome.Failed, (await fetcher.FetchAsync(new Uri("https://flaky.example/a"), default)).Outcome);
        Assert.Equal(FetchOutcome.Cached, (await fetcher.FetchAsync(new Uri("https://flaky.example/b"), default)).Outcome);
        Assert.Single(stub.Calls);

        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(FetchOutcome.Failed, (await fetcher.FetchAsync(new Uri("https://flaky.example/c"), default)).Outcome);
        Assert.Equal(2, stub.Calls.Count);
    }

    [Fact]
    public async Task A_transport_error_and_a_timeout_are_failures_not_exceptions()
    {
        var (broken, _, _) = Build(_ => throw new HttpRequestException("connection reset"));
        Assert.Equal(FetchOutcome.Failed, (await broken.FetchAsync(new Uri("https://reset.example/"), default)).Outcome);

        SafeFetcher.Timeout = TimeSpan.FromMilliseconds(150);
        var hanging = new SafeFetcher(new HttpClient(new HangingHandler()), Options.Create(new FeedEaterOptions()), new CursorStore(pg.Db), new FakeTimeProvider(Now), NullLogger<SafeFetcher>.Instance);
        Assert.Equal(FetchOutcome.Failed, (await hanging.FetchAsync(new Uri("https://slow.example/"), default)).Outcome);
    }

    [Fact]
    public async Task A_refused_address_from_the_connector_surfaces_as_refused()
    {
        var (fetcher, _, _) = Build(_ => throw new HttpRequestException("blocked", new UnsafeAddressException("evil.example")));

        Assert.Equal(FetchOutcome.Refused, (await fetcher.FetchAsync(new Uri("https://evil.example/"), default)).Outcome);
    }

    [Fact]
    public async Task One_request_a_second_per_host()
    {
        var (fetcher, stub, time) = Build(_ => Html("x"));

        await fetcher.FetchAsync(new Uri("https://same.example/1"), default);
        var second = fetcher.FetchAsync(new Uri("https://same.example/2"), default);
        var other = await fetcher.FetchAsync(new Uri("https://different.example/1"), default);   // another host does not wait
        await Task.Delay(50);

        Assert.True(other.Ok);
        Assert.False(second.IsCompleted);
        Assert.Equal(2, stub.Calls.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await second).Ok);
    }

    [Fact]
    public async Task The_daily_cap_stops_fetching_and_survives_a_new_instance()
    {
        var (fetcher, stub, time) = Build(_ => Html("x"), o => o.MaxPerDay = 2);
        var later = () => time.Advance(TimeSpan.FromSeconds(2));

        Assert.True((await fetcher.FetchAsync(new Uri("https://a.example/"), default)).Ok);
        Assert.True((await fetcher.FetchAsync(new Uri("https://b.example/"), default)).Ok);
        Assert.Equal(FetchOutcome.CapReached, (await fetcher.FetchAsync(new Uri("https://c.example/"), default)).Outcome);
        Assert.Equal(2, stub.Calls.Count);

        var (restarted, _, _) = Build(_ => Html("x"), o => o.MaxPerDay = 2);   // a restart keeps the count in the database
        Assert.Equal(FetchOutcome.CapReached, (await restarted.FetchAsync(new Uri("https://d.example/"), default)).Outcome);

        time.Advance(TimeSpan.FromDays(1));
        later();
        Assert.True((await fetcher.FetchAsync(new Uri("https://e.example/"), default)).Ok);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }
}
