using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Library;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Profiles;
using FeedEater.Sources;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LibraryEnricherTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Page = string.Join(' ', Enumerable.Repeat("Postgres async I/O details.", 80));

    private int _chatCalls;
    private int _embedCalls;
    private bool _modelDown;
    private bool _embedderDown;
    private static long _entry = 300;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpResponseMessage Llm(string body)
    {
        if (body.Contains("\"input\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _embedCalls);
            return _embedderDown ? StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable) : StubHandler.Json(TestVectors.EmbeddingResponse(1));
        }

        Interlocked.Increment(ref _chatCalls);
        if (_modelDown)
        {
            return StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        }

        var content = """{"relevance":2,"summary":"It is about Postgres.","why":"Box runs it.","kind":"fyi","project":null,"suggestion":null}""";
        return StubHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
            usage = new { prompt_tokens = 100, completion_tokens = 20 },
        }));
    }

    private LibraryEnricher Build()
    {
        var options = Options.Create(new FeedEaterOptions
        {
            ProfilePath = "Fixtures/profile.json",
            Miniflux = new MinifluxOptions { BaseUrl = "http://miniflux/", Token = "t" },
        });
        var time = new FakeTimeProvider(Now);
        var miniflux = new StubHandler((_, _) => StubHandler.Json(JsonSerializer.Serialize(new { content = $"<p>{Page}</p>" })));
        var llm = new LiteLlmClient(new StubHandler((_, body) => Llm(body)).Client("http://llm/"), new UsageStore(pg.Db), options);
        var items = new ItemStore(pg.Db);
        var analysis = new AnalysisStore(pg.Db);
        return new LibraryEnricher(
            new LibraryStore(pg.Db), items,
            new ItemText(items, options, new MinifluxClient(miniflux.Client("http://miniflux/")), NullLogger<ItemText>.Instance),
            new ItemReader(analysis, new ProfileStore(pg.Db), llm, options), analysis, llm, options,
            new LoopHealth(time), time, NullLogger<LibraryEnricher>.Instance);
    }

    private async Task<long> SaveAsync(string title = "Saved", string content = "short", bool embedded = false, bool read = false)
    {
        var id = await Seed.ItemAsync(pg, 1, title, TestVectors.OneHot(0), content: content, entryId: Interlocked.Increment(ref _entry));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        if (!embedded)
        {
            await c.ExecuteAsync("update items set embedding = null where id = @id", new { id });
        }

        await new LibraryStore(pg.Db).AddAsync(id, "ui", Now, default);
        if (read)
        {
            await Seed.ReadAsync(pg, id, "homelab", "fyi");
        }

        return id;
    }

    private async Task<T?> ScalarAsync<T>(string sql, object args)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task A_saved_item_gets_full_text_a_read_and_an_embedding_in_one_pass()
    {
        var id = await SaveAsync();

        Assert.Equal(1, await Build().RunAsync(default));

        Assert.True(await ScalarAsync<int>("select length(content) from items where id = @id", new { id }) > 1000);
        Assert.Equal("It is about Postgres.", await ScalarAsync<string>("select summary from reads where item_id = @id", new { id }));
        Assert.True(await ScalarAsync<bool>("select embedding is not null from items where id = @id", new { id }));
        Assert.True(await ScalarAsync<bool>("select enriched_at is not null from saved where item_id = @id", new { id }));
        Assert.Equal(1, await ScalarAsync<int>("select enrich_attempts from saved where item_id = @id", new { id }));
    }

    [Fact]
    public async Task A_failing_model_leaves_the_item_saved_counts_an_attempt_and_still_embeds()
    {
        _modelDown = true;
        var id = await SaveAsync();

        await Build().RunAsync(default);

        Assert.True(await new LibraryStore(pg.Db).IsSavedAsync(id, default));
        Assert.Equal(1, await ScalarAsync<int>("select enrich_attempts from saved where item_id = @id", new { id }));
        Assert.True(await ScalarAsync<bool>("select enriched_at is null from saved where item_id = @id", new { id }));
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from reads where item_id = @id", new { id }));
        Assert.True(await ScalarAsync<bool>("select embedding is not null from items where id = @id", new { id }));
    }

    [Fact]
    public async Task After_five_failures_the_item_is_no_longer_pending_but_stays_saved()
    {
        _embedderDown = true;
        var id = await SaveAsync();
        var enricher = Build();

        for (var i = 0; i < LibraryStore.MaxEnrichAttempts; i++)
        {
            Assert.Equal(1, await enricher.RunAsync(default));
        }

        Assert.Equal(0, await enricher.RunAsync(default));
        Assert.Empty(await new LibraryStore(pg.Db).PendingEnrichmentAsync(10, default));
        Assert.True(await new LibraryStore(pg.Db).IsSavedAsync(id, default));
        Assert.Equal(5, await ScalarAsync<int>("select enrich_attempts from saved where item_id = @id", new { id }));
    }

    [Fact]
    public async Task An_item_that_already_has_a_read_is_not_read_again()
    {
        await SaveAsync("Has read", read: true);
        var enricher = Build();
        await enricher.RunAsync(default);
        Assert.Equal(0, _chatCalls);

        await SaveAsync("Needs read");
        await Build().RunAsync(default);
        Assert.Equal(1, _chatCalls);
    }

    [Fact]
    public async Task An_item_that_is_already_embedded_is_not_embedded_again()
    {
        await SaveAsync(embedded: true, read: true);

        await Build().RunAsync(default);

        Assert.Equal(0, _embedCalls);
    }

    [Fact]
    public async Task A_pass_takes_at_most_the_batch_oldest_first_and_an_empty_pass_does_nothing()
    {
        Assert.Equal(0, await Build().RunAsync(default));
        for (var i = 0; i < 7; i++)
        {
            await SaveAsync($"Item {i}", embedded: true, read: true);
        }

        Assert.Equal(5, await Build().RunAsync(default));
        Assert.Equal(2, (await new LibraryStore(pg.Db).PendingEnrichmentAsync(10, default)).Count);
    }
}
