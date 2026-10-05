using Dapper;
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

    [Fact]
    public async Task Spend_is_grouped_by_local_day_and_by_purpose_and_model()
    {
        await pg.ResetAsync();
        var store = new UsageStore(pg.Db);
        await store.AddAsync("triage", "nano", 100, 20, 0.5m, default);
        await store.AddAsync("triage", "nano", 100, 20, 0.25m, default);
        await store.AddAsync("read", "haiku", 1000, 100, 1m, default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update llm_usage set at = timestamptz '2026-10-04 21:30:00+00' where purpose = 'read'");
            await c.ExecuteAsync("update llm_usage set at = timestamptz '2026-10-05 08:00:00+00' where purpose = 'triage'");
        }

        var since = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var days = await store.SpendByDayAsync(since, "Europe/Kyiv", default);
        var purposes = await store.SpendByPurposeAsync(since, default);

        // 21:30Z is 00:30 Kyiv on the 5th, so both land on the 5th; the day is the local one.
        Assert.Equal([(new DateOnly(2026, 10, 5), 1.75m)], days.Select(d => (d.Day, d.Cost)));
        Assert.Equal([("read", "haiku", 1, 1m), ("triage", "nano", 2, 0.75m)], purposes.Select(p => (p.Purpose, p.Model, p.Calls, p.Cost)));
    }
}
