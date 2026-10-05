using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SearchTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Hybrid_search_returns_meaning_matches_and_keyword_matches()
    {
        var store = new ItemStore(pg.Db);
        var byMeaning = await Seed.ItemAsync(pg, 1, "Tuning approximate nearest neighbour indexes", TestVectors.OneHot(1));
        var byWord = await Seed.ItemAsync(pg, 1, "HNSW in pgvector 0.8", TestVectors.OneHot(2));
        await Seed.ItemAsync(pg, 1, "Unrelated", TestVectors.OneHot(3));

        var both = await store.SearchAsync(TestVectors.OneHot(1), "hnsw", null, null, null, null, 2, default);
        var wordsOnly = await store.SearchAsync(null, "hnsw", null, null, null, null, 10, default);

        Assert.Equal([byMeaning, byWord], both.Select(h => h.Id).Order());
        Assert.Equal([byWord], wordsOnly.Select(h => h.Id));
    }

    [Fact]
    public async Task Filters_by_publication_range()
    {
        var store = new ItemStore(pg.Db);
        await Seed.ItemAsync(pg, 1, "hnsw old", TestVectors.OneHot(1), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = await Seed.ItemAsync(pg, 1, "hnsw new", TestVectors.OneHot(1), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var hits = await store.SearchAsync(null, "hnsw", null, null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, 10, default);

        Assert.Equal([recent], hits.Select(h => h.Id));
    }

    [Fact]
    public async Task Filters_by_project_and_kind()
    {
        var store = new ItemStore(pg.Db);
        var lab = await Seed.ItemAsync(pg, 1, "hnsw for homelab", TestVectors.OneHot(1));
        var other = await Seed.ItemAsync(pg, 1, "hnsw for chargehand", TestVectors.OneHot(1));
        await Seed.ReadAsync(pg, lab, "homelab", "improve");
        await Seed.ReadAsync(pg, other, "chargehand", "fyi");

        var byProject = await store.SearchAsync(null, "hnsw", "homelab", null, null, null, 10, default);
        var byKind = await store.SearchAsync(TestVectors.OneHot(1), "hnsw", null, "fyi", null, null, 10, default);

        Assert.Equal([lab], byProject.Select(h => h.Id));
        Assert.Equal([other], byKind.Select(h => h.Id));
    }
}
