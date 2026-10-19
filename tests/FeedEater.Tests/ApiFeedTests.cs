using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FeedEater.Api;
using FeedEater.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FeedEater.Tests;

/// <summary>The built-in reader: feeds can be added and removed. The fetch is stubbed.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiFeedTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static WebApplicationFactory<Program>? app;
    private static HttpClient? http;

    private WebApplicationFactory<Program> App => app ??= ApiHost.Create(pg.ConnectionString, s =>
        s.AddHttpClient(FeedEater.Fetch.SafeFetcher.FeedClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
            request.RequestUri!.Host == "good.example"
                ? SourceKit.Xml(SourceKit.Rss(("g1", "Entry", DateTimeOffset.UtcNow.AddHours(-1), "text")))
                : SourceKit.Status(HttpStatusCode.NotFound))));

    private HttpClient Http => http ??= App.CreateClient();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) =>
        await ApiHost.RawAsync(Http, method, path, "Bearer " + await ApiHost.TokenAsync(App), body: body is null ? null : JsonSerializer.Serialize(body));

    [Fact]
    public async Task Adding_a_feed_answers_201_lists_it_with_its_fetch_state_and_removing_it_is_204()
    {
        var added = await SendAsync(HttpMethod.Post, "/api/v1/feeds", new { url = "https://good.example/feed" });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var body = (await added.Content.ReadFromJsonAsync<AddFeedResponse>())!;
        Assert.Equal(("Feed title", false, 1), (body.Title, body.Existing, body.Items));
        var feeds = (await (await SendAsync(HttpMethod.Get, "/api/v1/feeds")).Content.ReadFromJsonAsync<List<FeedResponse>>())!;
        var feed = Assert.Single(feeds);
        Assert.Equal("https://good.example/feed", feed.FeedUrl);
        Assert.Equal(0, feed.FailCount);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/v1/feeds/{feed.Id}")).StatusCode);
        Assert.Empty((await (await SendAsync(HttpMethod.Get, "/api/v1/feeds")).Content.ReadFromJsonAsync<List<FeedResponse>>())!);
        var gone = await SendAsync(HttpMethod.Delete, $"/api/v1/feeds/{feed.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("application/problem+json", gone.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task An_address_that_is_no_feed_is_a_400_problem_with_the_reason()
    {
        var bad = await SendAsync(HttpMethod.Post, "/api/v1/feeds", new { url = "https://missing.example/feed" });
        var empty = await SendAsync(HttpMethod.Post, "/api/v1/feeds", new { });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (bad.StatusCode, empty.StatusCode));
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
        Assert.Empty((await (await SendAsync(HttpMethod.Get, "/api/v1/feeds")).Content.ReadFromJsonAsync<List<FeedResponse>>())!);
    }
}

/// <summary>Miniflux mode: the reader is not registered, so add and remove answer 409 (never 500); list and mute still work.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiMinifluxFeedTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static WebApplicationFactory<Program>? app;
    private static HttpClient? http;

    private WebApplicationFactory<Program> App => app ??= ApiHost.Create(pg.ConnectionString, null, ("FeedEater:Source:Kind", "miniflux"));
    private HttpClient Http => http ??= App.CreateClient();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) =>
        await ApiHost.RawAsync(Http, method, path, "Bearer " + await ApiHost.TokenAsync(App), body: body is null ? null : JsonSerializer.Serialize(body));

    [Fact]
    public async Task Adding_and_removing_a_feed_are_409_problems()
    {
        var feed = await Seed.ItemAsync(pg, 1, "Entry", TestVectors.OneHot(1));

        var add = await SendAsync(HttpMethod.Post, "/api/v1/feeds", new { url = "https://good.example/feed" });
        var remove = await SendAsync(HttpMethod.Delete, "/api/v1/feeds/1");

        foreach (var response in new[] { add, remove })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("Add or remove feeds in Miniflux", await response.Content.ReadAsStringAsync());
        }

        Assert.True(feed > 0);
    }

    [Fact]
    public async Task Listing_and_muting_work_without_the_reader()
    {
        await Seed.ItemAsync(pg, 1, "Entry", TestVectors.OneHot(1));

        var listed = await SendAsync(HttpMethod.Get, "/api/v1/feeds");
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync());
        var feed = Assert.Single((await listed.Content.ReadFromJsonAsync<List<FeedResponse>>())!);
        Assert.Null(feed.FailCount);
        Assert.Equal(1, feed.Items30d);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/feeds/{feed.Id}/mute", new { muted = true })).StatusCode);
        Assert.True(Assert.Single((await (await SendAsync(HttpMethod.Get, "/api/v1/feeds")).Content.ReadFromJsonAsync<List<FeedResponse>>())!).Muted);
    }
}

/// <summary>Background jobs on and the optional features configured: the run routes accept; follows off lists nothing and refuses a start.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiRunTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static WebApplicationFactory<Program>? app;
    private static HttpClient? http;

    private WebApplicationFactory<Program> App => app ??= ApiHost.Create(pg.ConnectionString, null,
        ("FeedEater:RunJobs", "true"), ("FeedEater:Follow:Enabled", "false"), ("FeedEater:Plane:BaseUrl", "http://plane.invalid"),
        ("FeedEater:Plane:Token", "t"), ("FeedEater:Plane:Workspace", "w"));
    private HttpClient Http => http ??= App.CreateClient();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) =>
        await ApiHost.RawAsync(Http, method, path, "Bearer " + await ApiHost.TokenAsync(App), body: body is null ? null : JsonSerializer.Serialize(body));

    private async Task<string?> CursorAsync(string name) => await new CursorStore(pg.Db).GetAsync(name, default);

    [Fact]
    public async Task The_digest_run_is_202_and_leaves_the_request_for_the_job()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/api/v1/digest/run", new { resend = true })).StatusCode);
        Assert.StartsWith("resend:", await CursorAsync("digest:force"));

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/api/v1/digest/run")).StatusCode);
        Assert.StartsWith("run:", await CursorAsync("digest:force"));
    }

    [Fact]
    public async Task The_ideas_run_is_202_and_sets_the_force_cursor()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/api/v1/ideas/run")).StatusCode);
        Assert.Equal("1", await CursorAsync("weekly-ideas:force"));
    }

    [Fact]
    public async Task With_follows_off_the_list_is_empty_and_a_start_is_409()
    {
        var item = await Seed.ItemAsync(pg, 1, "Story", TestVectors.OneHot(1));

        Assert.Equal("[]", await (await SendAsync(HttpMethod.Get, "/api/v1/follows")).Content.ReadAsStringAsync());
        var start = await SendAsync(HttpMethod.Post, "/api/v1/follows", new { itemId = item });
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Equal("application/problem+json", start.Content.Headers.ContentType!.MediaType);
    }
}
