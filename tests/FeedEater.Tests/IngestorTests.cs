using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class IngestorTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Ingestor Build(StubHandler miniflux, StubHandler llm, int pageSize = 100)
    {
        var options = Options.Create(new FeedEaterOptions { Miniflux = { PageSize = pageSize } });
        return new Ingestor(
            new MinifluxClient(miniflux.Client("http://miniflux/")),
            new ItemStore(pg.Db),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            options, new LoopHealth(TimeProvider.System), TimeProvider.System, NullLogger<Ingestor>.Instance);
    }

    private static StubHandler Miniflux() => new((request, _) => StubHandler.Json(
        request.RequestUri!.Query.Contains("after_entry_id=0", StringComparison.Ordinal)
            ? File.ReadAllText("Fixtures/miniflux-entries.json")
            : """{"total":0,"entries":[]}"""));

    /// <summary>Serves the fixture in pages of <paramref name="pageSize"/> by after_entry_id, like Miniflux.</summary>
    private static StubHandler PagedMiniflux(int pageSize) => new((request, _) =>
    {
        var query = request.RequestUri!.Query;
        var after = long.Parse(query.Split("after_entry_id=")[1].Split('&')[0]);
        var all = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText("Fixtures/miniflux-entries.json"))!["entries"]!.AsArray();
        var page = new System.Text.Json.Nodes.JsonArray(all
            .Where(e => e!["id"]!.GetValue<long>() > after).Take(pageSize)
            .Select(e => System.Text.Json.Nodes.JsonNode.Parse(e!.ToJsonString())).ToArray());
        return StubHandler.Json(new System.Text.Json.Nodes.JsonObject { ["total"] = page.Count, ["entries"] = page }.ToJsonString());
    });

    private static StubHandler Llm() => new((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));

    private sealed record Row
    {
        public long MinifluxEntryId { get; init; }
        public string CanonicalUrl { get; init; } = "";
        public string Content { get; init; } = "";
        public long? DuplicateOf { get; init; }
        public bool Embedded { get; init; }
    }

    [Fact]
    public async Task Stores_entries_marks_duplicates_and_embeds_everything()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var ingestor = Build(Miniflux(), Llm());

        Assert.Equal(5, await ingestor.IngestAsync(default));
        Assert.Equal(5, await ingestor.EmbedPendingAsync(default));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var rows = (await c.QueryAsync<Row>(
            """
            select i.miniflux_entry_id, i.canonical_url, i.content, d.miniflux_entry_id as duplicate_of, i.embedding is not null as embedded
            from items i left join items d on d.id = i.duplicate_of order by i.miniflux_entry_id
            """)).ToDictionary(r => r.MinifluxEntryId);

        Assert.Equal("https://postgresql.org/about/news/postgresql-181-released", rows[101].CanonicalUrl);
        Assert.Equal("Fixes for async I/O .", rows[101].Content);
        Assert.Null(rows[101].DuplicateOf);
        Assert.Equal(101, rows[102].DuplicateOf);   // same title, other feed
        Assert.Null(rows[103].DuplicateOf);         // empty title never matches
        Assert.Equal(101, rows[104].DuplicateOf);   // same canonical URL
        Assert.Null(rows[105].DuplicateOf);         // short title never matches the empty one
        Assert.All(rows.Values, r => Assert.True(r.Embedded));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>("select count(*)::int from feeds"));
    }

    [Fact]
    public async Task A_second_poll_starts_after_the_last_entry_and_adds_nothing()
    {
        var miniflux = Miniflux();
        var ingestor = Build(miniflux, Llm());

        await ingestor.IngestAsync(default);
        Assert.Equal(0, await ingestor.IngestAsync(default));

        Assert.Contains(miniflux.Calls, call => call.Uri.Contains("after_entry_id=105", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paging_walks_every_page_and_the_cursor_ends_at_the_last_entry()
    {
        var miniflux = PagedMiniflux(2);
        var ingestor = Build(miniflux, Llm(), pageSize: 2);

        Assert.Equal(5, await ingestor.IngestAsync(default));

        Assert.Equal(105, await new ItemStore(pg.Db).MaxEntryIdAsync(default));
        Assert.Equal(3, miniflux.Calls.Count);   // after 0, 102, 104; the last page is short
    }

    [Fact]
    public async Task A_rejected_input_is_skipped_and_does_not_stall_the_others()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var llm = new StubHandler((_, body) => body.Contains("Same article", StringComparison.Ordinal)
            ? StubHandler.Json("""{"error":{"message":"maximum context length exceeded"}}""", System.Net.HttpStatusCode.BadRequest)
            : StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var ingestor = Build(Miniflux(), llm);
        await ingestor.IngestAsync(default);

        Assert.Equal(4, await ingestor.EmbedPendingAsync(default));
        var callsAfterFirstPass = llm.Calls.Count;
        Assert.Equal(0, await ingestor.EmbedPendingAsync(default));
        Assert.Equal(callsAfterFirstPass, llm.Calls.Count);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var missing = (await c.QueryAsync<long>("select miniflux_entry_id from items where embedding is null")).ToList();
        Assert.Equal(104L, Assert.Single(missing));
    }

    [Fact]
    public async Task An_auth_error_from_the_embedder_throws_and_parks_nothing()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var denied = true;
        var llm = new StubHandler((_, body) => denied
            ? StubHandler.Json("""{"error":{"message":"invalid key"}}""", System.Net.HttpStatusCode.Unauthorized)
            : StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var ingestor = Build(Miniflux(), llm);
        await ingestor.IngestAsync(default);

        await Assert.ThrowsAsync<HttpRequestException>(() => ingestor.EmbedPendingAsync(default));
        denied = false;

        Assert.Equal(5, await ingestor.EmbedPendingAsync(default));
    }

    [Fact]
    public async Task An_outage_skips_nothing_and_the_next_pass_embeds_the_row()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var down = true;
        var llm = new StubHandler((_, body) => down && body.Contains("Same article", StringComparison.Ordinal)
            ? StubHandler.Json("{}", System.Net.HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var ingestor = Build(Miniflux(), llm);
        await ingestor.IngestAsync(default);

        await Assert.ThrowsAsync<HttpRequestException>(() => ingestor.EmbedPendingAsync(default));
        down = false;

        Assert.Equal(5, await ingestor.EmbedPendingAsync(default));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*)::int from items where embedding is null"));
    }

    [Fact]
    public async Task A_page_size_of_zero_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Build(Miniflux(), Llm(), pageSize: 0).IngestAsync(default));
    }

    [Fact]
    public void Embed_text_falls_back_to_the_url_and_is_capped()
    {
        Assert.Equal("https://x.example/", Ingestor.EmbedText("", "", "https://x.example/", 8000));
        Assert.Equal(8 + 1 + 5, Ingestor.EmbedText("Headline", new string('a', 50), "u", 5).Length);
    }
}
