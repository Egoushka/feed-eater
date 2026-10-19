using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FeedEater.Inbox;
using FeedEater.Storage;
using FeedEater.Swipe;
using FeedEater.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FeedEater.Tests;

/// <summary>One host per class: every extra host adds a connection pool, and the shared Postgres has a low connection limit.</summary>
internal static class ApiHost
{
    public static WebApplicationFactory<Program> Create(string connectionString, Action<IServiceCollection>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            (string Key, string Value)[] settings =
            [
                ("ConnectionStrings:FeedEater", connectionString),
                ("Mcp:Token", "api-test-token"),
                ("FeedEater:RunJobs", "false"),
                ("FeedEater:ProfilePath", "/nope/profile.json"),
                ("FeedEater:Swipe:Enabled", "true"),
            ];
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IHostedService>();
                configure?.Invoke(s);
            });
        });

    public static async Task<string> TokenAsync(WebApplicationFactory<Program> app) =>
        (await app.Services.GetRequiredService<ApiTokenStore>().CreateAsync("test", [ApiScopes.App], default)).Token;

    public static async Task<HttpResponseMessage> RawAsync(HttpClient http, HttpMethod method, string path, string? authorization, string? origin = null, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await http.SendAsync(request);
    }
}

// Refusal cases spend the host's anonymous budget (20 a minute); keep them few or give them their own host.
[Collection(PostgresCollection.Name)]
public sealed class ApiTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static WebApplicationFactory<Program>? app;
    private static HttpClient? http;

    // Embeddings fail (search falls back to keywords), chat answers with Chat, article fetches fail.
    private static string? Chat = "";

    private WebApplicationFactory<Program> App => app ??= ApiHost.Create(pg.ConnectionString, s =>
    {
        var model = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("chat/completions", StringComparison.Ordinal)
            ? Chat is null ? throw new HttpRequestException("model down") : StubHandler.Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Chat } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } }))
            : StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        s.AddHttpClient<FeedEater.Llm.LiteLlmClient>().ConfigurePrimaryHttpMessageHandler(() => model);
        s.AddHttpClient(FeedEater.Fetch.SafeFetcher.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.NotFound)));
    });
    private HttpClient Http => http ??= App.CreateClient();

    private Task<string> TokenAsync() => ApiHost.TokenAsync(App);

    private Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, string? authorization, string? origin = null, string? body = null) =>
        ApiHost.RawAsync(Http, method, path, authorization, origin, body);

    private async Task<T> GetAsync<T>(string path)
    {
        var response = await RawAsync(HttpMethod.Get, path, "Bearer " + await TokenAsync());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string path, object body)
    {
        var response = await RawAsync(HttpMethod.Post, path, "Bearer " + await TokenAsync(), body: JsonSerializer.Serialize(body));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task RankedItemsAsync(int n)
    {
        for (var i = 0; i < n; i++)
        {
            await Seed.ItemAsync(pg, 1, $"Item {i}", TestVectors.OneHot(100 + i), DateTime.UtcNow.AddHours(-5));
        }
    }

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("no row");
    }

    private async Task RevokeAllAsync()
    {
        var store = App.Services.GetRequiredService<ApiTokenStore>();
        foreach (var row in await store.ListAsync(default))
        {
            await store.RevokeAsync(row.Id, default);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic abc")]
    [InlineData("bearer {t}")]
    [InlineData("Bearer fe_unknown")]
    public async Task Requests_without_an_accepted_bearer_token_get_401(string? header)
    {
        var token = await TokenAsync();
        var response = await RawAsync(HttpMethod.Get, "/api/v1/status", header?.Replace("{t}", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_second_space_after_the_scheme_gets_401()
    {
        // HttpClient folds the double space away, so the header is set on the server side of the test host.
        var token = await TokenAsync();
        var context = await App.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/v1/status";
            c.Request.Headers.Authorization = "Bearer  " + token;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_gets_401()
    {
        var token = await TokenAsync();
        await RevokeAllAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawAsync(HttpMethod.Get, "/api/v1/status", "Bearer " + token)).StatusCode);
    }

    [Fact]
    public async Task A_request_with_an_Origin_is_refused_even_with_a_good_token()
    {
        var response = await RawAsync(HttpMethod.Get, "/api/v1/status", "Bearer " + await TokenAsync(), origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_reports_version_and_gate()
    {
        var status = await GetAsync<StatusResponse>("/api/v1/status");
        Assert.False(string.IsNullOrEmpty(status.Version));
        Assert.Equal("open", status.Gate);
        Assert.True(status.Swipe);
        Assert.Null(status.Digest);
    }

    [Fact]
    public async Task Peek_returns_later_cards_without_counting_them_shown()
    {
        await RankedItemsAsync(5);
        var first = await PostAsync<NextResult>("/api/v1/swipe/next", new { });
        var ahead = await PostAsync<NextResult>("/api/v1/swipe/next", new { position = 3, peek = true });

        Assert.Equal(3, ahead.Card!.Position);
        Assert.Equal(1, await ScalarAsync<int>("select cards_shown from swipe_sessions where id = @id", new { id = first.Card!.Session }));
    }

    [Fact]
    public async Task An_event_on_a_peeked_card_counts_it_shown_and_then_acts_work()
    {
        await RankedItemsAsync(5);
        var s = (await PostAsync<NextResult>("/api/v1/swipe/next", new { })).Card!.Session;
        await PostAsync<NextResult>("/api/v1/swipe/next", new { position = 2, peek = true });

        await PostAsync<JsonElement>("/api/v1/swipe/event", new { session = s, position = 2, direction = "up" });
        var act = await PostAsync<ActResult>("/api/v1/swipe/act", new { session = s, position = 2, act = "like", clientId = Guid.NewGuid() });

        Assert.True(act.Ok);
        Assert.Equal(3, await ScalarAsync<int>("select cards_shown from swipe_sessions where id = @s", new { s }));
    }

    [Fact]
    public async Task Peek_never_starts_a_session()
    {
        await RankedItemsAsync(5);
        var r = await PostAsync<NextResult>("/api/v1/swipe/next", new { position = 0, peek = true });

        Assert.Equal("closed", r.Type);
        Assert.Equal("not-started", r.Closed!.Why);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from swipe_sessions"));
    }

    [Fact]
    public async Task Peeked_inbox_cards_are_counted_shown_by_their_own_events_in_any_order()
    {
        var inbox = App.Services.GetRequiredService<InboxStore>();
        for (var i = 0; i < 4; i++)
        {
            await inbox.AddAsync(new InboxEntry("n" + i, InboxKinds.Notice, ExpiresAt: DateTimeOffset.UtcNow.AddDays(1)), DateTimeOffset.UtcNow.AddHours(-4 + i), default);
        }

        var s = (await PostAsync<NextResult>("/api/v1/swipe/next", new { })).Card!.Session;
        await PostAsync<NextResult>("/api/v1/swipe/next", new { position = 2, peek = true });
        await PostAsync<NextResult>("/api/v1/swipe/next", new { position = 3, peek = true });
        Assert.Equal(new[] { 1, 0, 0, 0 }, await ShownCountsAsync());

        await PostAsync<JsonElement>("/api/v1/swipe/event", new { session = s, position = 3, direction = "up" });
        await PostAsync<JsonElement>("/api/v1/swipe/event", new { session = s, position = 2, direction = "up" });

        Assert.Equal(new[] { 1, 0, 1, 1 }, await ShownCountsAsync());
    }

    private async Task<int[]> ShownCountsAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<int>("select shown_count from inbox order by created_at, id")).ToArray();
    }

    [Fact]
    public async Task Peek_past_the_cap_returns_the_summary_and_leaves_the_session_open()
    {
        await RankedItemsAsync(3);
        var s = (await PostAsync<NextResult>("/api/v1/swipe/next", new { })).Card!.Session;
        var r = await PostAsync<NextResult>("/api/v1/swipe/next", new { position = 99, peek = true });

        Assert.Equal("summary", r.Type);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from swipe_sessions where id = @s and ended_at is not null", new { s }));
    }

    [Fact]
    public async Task An_event_on_a_peeked_card_of_a_session_past_its_time_cap_does_not_count_it()
    {
        await RankedItemsAsync(5);
        var s = (await PostAsync<NextResult>("/api/v1/swipe/next", new { })).Card!.Session;
        await PostAsync<NextResult>("/api/v1/swipe/next", new { position = 2, peek = true });
        await SqlAsync("update swipe_sessions set started_at = now() - interval '1 day'");

        var r = await PostAsync<OkResponse>("/api/v1/swipe/event", new { session = s, position = 2, direction = "up" });

        Assert.False(r.Ok);
        Assert.Equal(1, await ScalarAsync<int>("select cards_shown from swipe_sessions where id = @s", new { s }));
    }

    [Fact]
    public async Task The_same_act_twice_over_the_API_votes_once()
    {
        await RankedItemsAsync(1);
        var card = (await PostAsync<NextResult>("/api/v1/swipe/next", new { })).Card!;
        var id = Guid.NewGuid();

        var first = await PostAsync<ActResult>("/api/v1/swipe/act", new { session = card.Session, position = card.Position, act = "like", clientId = id });
        await SqlAsync("update votes set at = now() - interval '1 hour'");
        var second = await PostAsync<ActResult>("/api/v1/swipe/act", new { session = card.Session, position = card.Position, act = "like", clientId = id });

        Assert.Equal(first, second);
        Assert.True(await ScalarAsync<DateTime>("select at from votes") < DateTime.UtcNow.AddMinutes(-30));
    }

    [Fact]
    public async Task A_body_over_64_KB_is_413()
    {
        var response = await RawAsync(HttpMethod.Post, "/api/v1/swipe/event", "Bearer " + await TokenAsync(),
            body: "{\"direction\":\"" + new string('x', 70_000) + "\"}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task The_OpenAPI_document_lists_the_status_route_and_needs_no_token()
    {
        var doc = await Http.GetStringAsync("/api/v1/openapi.json");
        Assert.Contains("/api/v1/status", doc);
        Assert.Contains("bearer", doc);
        Assert.DoesNotContain("/ui", doc);
        Assert.DoesNotContain("\"/swipe", doc);
        Assert.Contains("/api/v1/swipe/next", doc);
        Assert.DoesNotContain("/healthz", doc);
    }

    [Fact]
    public async Task An_item_is_returned_with_its_read_and_not_marked_full_text_when_the_page_cannot_be_fetched()
    {
        var id = await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1), content: "Short teaser.");
        await Seed.ReadAsync(pg, id, "homelab", "improve");

        var item = await GetAsync<ItemResponse>($"/api/v1/items/{id}");

        Assert.Equal(id, item.Id);
        Assert.Equal("Quokka backups", item.Title);
        Assert.Equal("Feed 1", item.Feed);
        Assert.Equal("Short teaser.", item.Text);
        Assert.False(item.FullText);
        Assert.Equal("homelab", item.Project);
        Assert.Equal("improve", item.Kind);
        Assert.False(item.Saved);
    }

    [Fact]
    public async Task A_missing_item_is_a_404_problem_and_an_empty_question_a_400()
    {
        var auth = "Bearer " + await TokenAsync();
        var missing = await RawAsync(HttpMethod.Get, "/api/v1/items/999999", auth);
        var empty = await RawAsync(HttpMethod.Post, "/api/v1/ask", auth, body: """{"question":"  "}""");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Asking_the_archive_returns_the_answer_with_cited_items_that_exist()
    {
        var id = await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1), content: "Quokka backups run nightly.");
        Chat = "Nightly [1], per nobody [7].";

        var result = await PostAsync<AskResponse>("/api/v1/ask", new { question = "quokka" });

        Assert.Equal("Nightly [1], per nobody .", result.Answer);
        Assert.Equal([id], result.Citations.Select(c => c.Hit.Id));
        Assert.Equal([1], result.Citations.Select(c => c.Number));
        Assert.Contains("[1]", result.Answer);
    }

    [Fact]
    public async Task An_over_long_question_is_a_400_and_a_failing_model_is_a_502_problem()
    {
        var auth = "Bearer " + await TokenAsync();
        var tooLong = await RawAsync(HttpMethod.Post, "/api/v1/ask", auth, body: JsonSerializer.Serialize(new { question = new string('q', 501) }));
        await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1), content: "Quokka backups run nightly.");
        Chat = null;
        var down = await RawAsync(HttpMethod.Post, "/api/v1/ask", auth, body: """{"question":"quokka"}""");
        Chat = "";

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Equal("application/problem+json", down.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Asking_about_an_item_returns_the_trimmed_answer_and_a_missing_item_is_a_404()
    {
        var id = await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1), content: "Quokka backups run nightly.");
        Chat = "  Nightly.  ";

        var result = await PostAsync<ItemAskResponse>($"/api/v1/items/{id}/ask", new { question = "when?" });
        var missing = await RawAsync(HttpMethod.Post, "/api/v1/items/999999/ask", "Bearer " + await TokenAsync(), body: """{"question":"when?"}""");

        Assert.Equal("Nightly.", result.Answer);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Search_filters_by_kind()
    {
        var improve = await Seed.ItemAsync(pg, 1, "Quokka backups", TestVectors.OneHot(1));
        var learn = await Seed.ItemAsync(pg, 1, "Quokka habitat", TestVectors.OneHot(2));
        await Seed.ReadAsync(pg, improve, "homelab", "improve");
        await Seed.ReadAsync(pg, learn, "homelab", "learn");

        var all = await GetAsync<List<ApiHit>>("/api/v1/search?q=quokka");
        var learning = await GetAsync<List<ApiHit>>("/api/v1/search?q=quokka&kind=learn&limit=500");

        Assert.Equal(2, all.Count);
        Assert.Equal([learn], learning.Select(h => h.Id));
        Assert.Equal(TimeSpan.Zero, all[0].PublishedAt.Offset);

        var json = JsonDocument.Parse(await (await RawAsync(HttpMethod.Get, "/api/v1/search?q=quokka", "Bearer " + await TokenAsync())).Content.ReadAsStringAsync());
        var published = json.RootElement[0].GetProperty("publishedAt").GetString()!;
        Assert.True(published.EndsWith('Z') || published.EndsWith("+00:00", StringComparison.Ordinal), published);
        Assert.False(json.RootElement[0].TryGetProperty("rank", out _));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ApiRateLimitTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Its own host, so the limiter's window does not leak into the other API tests.
    private static WebApplicationFactory<Program>? app;

    [Fact]
    public async Task The_121st_request_in_a_minute_is_429()
    {
        app ??= ApiHost.Create(pg.ConnectionString);
        var http = app.CreateClient();
        var token = await ApiHost.TokenAsync(app);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 121; i++)
        {
            last = await ApiHost.RawAsync(http, HttpMethod.Get, "/api/v1/status", "Bearer " + token);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Equal("application/problem+json", last.Content.Headers.ContentType!.MediaType);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ApiAnonymousLimitTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Its own host: the anonymous budget is shared by every refusal on a host.
    private static WebApplicationFactory<Program>? app;

    [Fact]
    public async Task Unknown_tokens_share_20_a_minute_and_then_even_a_valid_token_gets_429()
    {
        app ??= ApiHost.Create(pg.ConnectionString);
        var http = app.CreateClient();
        var token = await ApiHost.TokenAsync(app);
        for (var i = 0; i < 20; i++)
        {
            var refused = await ApiHost.RawAsync(http, HttpMethod.Get, "/api/v1/status", "Bearer fe_guess" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await ApiHost.RawAsync(http, HttpMethod.Get, "/api/v1/status", "Bearer fe_guess")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ApiHost.RawAsync(http, HttpMethod.Get, "/api/v1/status", "Bearer " + token)).StatusCode);
    }
}
