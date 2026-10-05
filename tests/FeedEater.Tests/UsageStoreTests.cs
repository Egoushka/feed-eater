using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UsageStoreTests(PostgresFixture pg)
{
    [Fact]
    public async Task Spend_since_sums_tiny_embedding_costs_without_rounding_them_away()
    {
        await pg.ResetAsync();
        var store = new UsageStore(pg.Db);

        await store.AddAsync("embed", "text-embedding-3-small", 10, 0, 0.00000002m, default);

        Assert.Equal(0.00000002m, await store.SpendSinceAsync(DateTimeOffset.UtcNow.AddHours(-1), default));
        Assert.Equal(0m, await store.SpendSinceAsync(DateTimeOffset.UtcNow.AddHours(1), default));
    }
}
