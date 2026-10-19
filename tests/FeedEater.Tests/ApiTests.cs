using System.Net;
using System.Net.Http.Json;
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
    public static WebApplicationFactory<Program> Create(string connectionString) =>
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

            b.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
        });

    public static async Task<string> TokenAsync(WebApplicationFactory<Program> app) =>
        (await app.Services.GetRequiredService<ApiTokenStore>().CreateAsync("test", [ApiScopes.App], default)).Token;

    public static async Task<HttpResponseMessage> RawAsync(HttpClient http, HttpMethod method, string path, string? authorization, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
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

[Collection(PostgresCollection.Name)]
public sealed class ApiTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static WebApplicationFactory<Program>? app;
    private static HttpClient? http;

    private WebApplicationFactory<Program> App => app ??= ApiHost.Create(pg.ConnectionString);
    private HttpClient Http => http ??= App.CreateClient();

    private Task<string> TokenAsync() => ApiHost.TokenAsync(App);

    private Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, string? authorization, string? origin = null) =>
        ApiHost.RawAsync(Http, method, path, authorization, origin);

    private async Task<T> GetAsync<T>(string path)
    {
        var response = await RawAsync(HttpMethod.Get, path, "Bearer " + await TokenAsync());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
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
    public async Task The_OpenAPI_document_lists_the_status_route_and_needs_no_token()
    {
        var doc = await Http.GetStringAsync("/api/v1/openapi.json");
        Assert.Contains("/api/v1/status", doc);
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

    [Fact]
    public async Task Unknown_tokens_share_a_budget_of_20_a_minute()
    {
        app ??= ApiHost.Create(pg.ConnectionString);
        var http = app.CreateClient();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 21; i++)
        {
            last = await ApiHost.RawAsync(http, HttpMethod.Get, "/api/v1/status", "Bearer fe_guess" + i);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
    }
}
