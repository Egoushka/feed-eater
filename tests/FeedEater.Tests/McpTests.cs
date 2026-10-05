using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using FeedEater.Llm;
using FeedEater.Mcp;
using FeedEater.Search;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class McpTests(PostgresFixture pg) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", "test-token")
            .UseSetting("FeedEater:RunJobs", "false")
            .UseSetting("FeedEater:Llm:BaseUrl", "http://127.0.0.1:9/"));   // nothing listens: search must fall back to keywords
    }

    public async Task DisposeAsync()
    {
        ArchiveSearch.EmbedTimeout = TimeSpan.FromSeconds(5);
        await _app.DisposeAsync();
    }

    private static HttpRequestMessage Rpc(string method, string paramsJson, string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{paramsJson}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    private async Task<string> CallAsync(string tool, string args) =>
        await (await _app.CreateClient().SendAsync(Rpc("tools/call", $$"""{"name":"{{tool}}","arguments":{{args}}}""", "test-token"))).Content.ReadAsStringAsync();

    [Fact]
    public async Task Rejects_missing_token_wrong_token_and_browser_origin()
    {
        var http = _app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Rpc("tools/list", "{}", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Rpc("tools/list", "{}", "nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Rpc("tools/list", "{}", "test-token", "https://evil.example"))).StatusCode);
    }

    [Fact]
    public async Task Missing_item_is_a_200_json_rpc_reply()
    {
        var response = await _app.CreateClient().SendAsync(Rpc("tools/call", """{"name":"feed_read","arguments":{"id":999999}}""", "test-token"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("jsonrpc", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_the_five_tools()
    {
        var body = await (await _app.CreateClient().SendAsync(Rpc("tools/list", "{}", "test-token"))).Content.ReadAsStringAsync();

        foreach (var tool in new[] { "feed_search", "feed_read", "feed_digests", "feed_digest", "feed_ideas" })
        {
            Assert.Contains(tool, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Search_answers_from_keywords_when_embeddings_are_unavailable_and_read_returns_the_text()
    {
        var id = await Seed.ItemAsync(pg, 1, "pgvector HNSW tuning", TestVectors.OneHot(1), content: "Set ef_search higher.");

        var search = await CallAsync("feed_search", """{"query":"hnsw"}""");
        var read = await CallAsync("feed_read", $$"""{"id":{{id}}}""");

        Assert.Contains("pgvector HNSW tuning", search, StringComparison.Ordinal);
        Assert.Contains("Set ef_search higher.", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_arguments_are_explained()
    {
        Assert.Contains("no item", await CallAsync("feed_read", """{"id":999}"""), StringComparison.Ordinal);
        Assert.Contains("yyyy-MM-dd", await CallAsync("feed_digest", """{"date":"5 Oct"}"""), StringComparison.Ordinal);
        Assert.Contains("query is empty", await CallAsync("feed_search", """{"query":" "}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_every_request_when_no_token_is_configured()
    {
        await using var open = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", "")
            .UseSetting("FeedEater:RunJobs", "false"));
        var http = open.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Rpc("tools/list", "{}", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Rpc("tools/list", "{}", ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Rpc("tools/list", "{}", "Bearer "))).StatusCode);
    }

    [Fact]
    public async Task Search_falls_back_to_keywords_when_the_embedder_hangs()
    {
        ArchiveSearch.EmbedTimeout = TimeSpan.FromMilliseconds(200);
        await using var hanging = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", "test-token")
            .UseSetting("FeedEater:RunJobs", "false")
            .ConfigureTestServices(s => s.AddHttpClient<LiteLlmClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new HangingHandler())));
        await Seed.ItemAsync(pg, 1, "pgvector HNSW tuning", TestVectors.OneHot(1));

        var started = DateTime.UtcNow;
        var response = await hanging.CreateClient().SendAsync(Rpc("tools/call", """{"name":"feed_search","arguments":{"query":"hnsw"}}""", "test-token"));

        Assert.Contains("pgvector HNSW tuning", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
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
