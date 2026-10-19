using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FeedEater.Api;
using FeedEater.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LibraryApiTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static WebApplicationFactory<Program>? app;
    private static HttpClient? http;

    private WebApplicationFactory<Program> App => app ??= ApiHost.Create(pg.ConnectionString);
    private HttpClient Http => http ??= App.CreateClient();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) =>
        await ApiHost.RawAsync(Http, method, path, "Bearer " + await ApiHost.TokenAsync(App), body: body is null ? null : JsonSerializer.Serialize(body));

    private async Task<List<LibraryEntry>> ListAsync(string query = "") =>
        (await (await SendAsync(HttpMethod.Get, "/api/v1/library" + query)).Content.ReadFromJsonAsync<List<LibraryEntry>>())!;

    [Fact]
    public async Task Saving_a_url_is_201_then_200_with_the_same_item_and_one_row()
    {
        var first = await SendAsync(HttpMethod.Post, "/api/v1/library", new { url = "https://example.com/a?utm_source=x", title = "A page" });
        var second = await SendAsync(HttpMethod.Post, "/api/v1/library", new { url = "https://example.com/a" });

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var one = (await first.Content.ReadFromJsonAsync<LibrarySaveResponse>())!;
        var two = (await second.Content.ReadFromJsonAsync<LibrarySaveResponse>())!;
        Assert.Equal((false, true, one.ItemId), (one.Existing, two.Existing, two.ItemId));
        var entry = Assert.Single(await ListAsync());
        Assert.Equal(("A page", "share"), (entry.Title, entry.Source));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("http://")]
    public async Task A_hostile_url_is_a_400_problem_and_saves_nothing(string url)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/library", new { url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Only http and https links can be saved", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task A_missing_url_and_over_long_title_text_or_note_are_400_problems()
    {
        var item = await Seed.ItemAsync(pg, 1, "Seeded", TestVectors.OneHot(1));
        await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}");

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/v1/library", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/v1/library", new { url = "https://example.com/t", title = new string('t', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/v1/library", new { url = "https://example.com/t", text = new string('x', 60_001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, $"/api/v1/library/{item}/note", new { note = new string('n', 2_001) })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/api/v1/library", new { url = "https://example.com/t", title = new string('t', 500) })).StatusCode);
    }

    [Fact]
    public async Task Saving_an_existing_item_is_201_then_200_and_404_when_there_is_none()
    {
        var item = await Seed.ItemAsync(pg, 1, "Seeded", TestVectors.OneHot(1));

        var first = await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}");
        var second = await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}");
        var none = await SendAsync(HttpMethod.Post, "/api/v1/library/99999");

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.NotFound), (first.StatusCode, second.StatusCode, none.StatusCode));
        Assert.Equal("application/problem+json", none.Content.Headers.ContentType!.MediaType);
        var again = (await second.Content.ReadFromJsonAsync<LibrarySaveResponse>())!;
        Assert.Equal((item, true), (again.ItemId, again.Existing));
        Assert.Equal("api", Assert.Single(await ListAsync()).Source);
    }

    [Fact]
    public async Task The_list_filters_by_state_and_pages_newest_first()
    {
        var ids = new List<long>();
        for (var n = 0; n < 3; n++)
        {
            var id = await Seed.ItemAsync(pg, 1, $"Item {n}", TestVectors.OneHot(n));
            ids.Add(id);
            await SendAsync(HttpMethod.Post, $"/api/v1/library/{id}");
            await Task.Delay(20);
        }

        await SendAsync(HttpMethod.Post, $"/api/v1/library/{ids[0]}/read", new { read = true });

        Assert.Equal([ids[2], ids[1]], (await ListAsync()).Select(e => e.ItemId));
        Assert.Equal([ids[2], ids[1]], (await ListAsync("?state=unread")).Select(e => e.ItemId));
        Assert.Equal([ids[0]], (await ListAsync("?state=read")).Select(e => e.ItemId));
        var all = await ListAsync("?state=all");
        Assert.Equal([ids[2], ids[1], ids[0]], all.Select(e => e.ItemId));
        Assert.NotNull(all[2].ReadAt);
        Assert.Equal([ids[2]], (await ListAsync("?state=all&limit=1")).Select(e => e.ItemId));
        Assert.Equal([ids[1], ids[0]], (await ListAsync($"?state=all&after={ids[2]}")).Select(e => e.ItemId));
        Assert.Equal([ids[0]], (await ListAsync($"?state=all&after={ids[1]}&limit=5")).Select(e => e.ItemId));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/api/v1/library?state=nope")).StatusCode);
        Assert.Single(await ListAsync("?state=all&limit=0"));
        Assert.Equal(3, (await ListAsync("?state=all&limit=101")).Count);
    }

    [Fact]
    public async Task Read_note_and_delete_round_trip_with_404_for_items_that_are_not_saved()
    {
        var item = await Seed.ItemAsync(pg, 1, "Seeded", TestVectors.OneHot(1));
        await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}/read", new { read = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/api/v1/library/{item}/note", new { note = "check later" })).StatusCode);
        var entry = Assert.Single(await ListAsync("?state=all"));
        Assert.Equal(("check later", true), (entry.Note, entry.ReadAt is not null));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}/read", new { read = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/api/v1/library/{item}/note", new { note = (string?)null })).StatusCode);
        entry = Assert.Single(await ListAsync("?state=all"));
        Assert.Equal((null, null), (entry.Note, entry.ReadAt));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/v1/library/{item}")).StatusCode);
        Assert.Empty(await ListAsync("?state=all"));
        foreach (var response in new[]
        {
            await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}/read", new { read = true }),
            await SendAsync(HttpMethod.Put, $"/api/v1/library/{item}/note", new { note = "x" }),
            await SendAsync(HttpMethod.Delete, $"/api/v1/library/{item}"),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"/api/v1/library/{item}/read", new { })).StatusCode);
    }

    [Fact]
    public async Task Search_with_saved_true_returns_only_saved_items_over_the_API_and_MCP()
    {
        var saved = await Seed.ItemAsync(pg, 1, "hnsw saved", TestVectors.OneHot(1));
        await Seed.ItemAsync(pg, 1, "hnsw not saved", TestVectors.OneHot(2));
        await SendAsync(HttpMethod.Post, $"/api/v1/library/{saved}");

        var all = (await (await SendAsync(HttpMethod.Get, "/api/v1/search?q=hnsw")).Content.ReadFromJsonAsync<List<ApiHit>>())!;
        var only = (await (await SendAsync(HttpMethod.Get, "/api/v1/search?q=hnsw&saved=true")).Content.ReadFromJsonAsync<List<ApiHit>>())!;
        var tools = ActivatorUtilities.CreateInstance<FeedTools>(App.Services);
        var viaMcp = JsonDocument.Parse(await tools.SearchAsync("hnsw", saved: true)).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt64());

        Assert.Equal(2, all.Count);
        Assert.Equal([true], all.Where(h => h.Id == saved).Select(h => h.Saved));
        Assert.Equal([saved], only.Select(h => h.Id));
        Assert.Equal([saved], viaMcp);
    }
}
