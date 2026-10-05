using Dapper;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SourceStatsTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Counts_items_candidates_digest_appearances_and_votes_per_feed_in_the_window()
    {
        var items = new ItemStore(pg.Db);
        var now = DateTime.UtcNow;
        var liked = await Seed.ItemAsync(pg, 1, "liked", TestVectors.OneHot(0), now.AddDays(-1));
        var shown = await Seed.ItemAsync(pg, 1, "shown", TestVectors.OneHot(0), now.AddDays(-2));
        await Seed.ItemAsync(pg, 1, "plain", TestVectors.OneHot(0), now.AddDays(-3));
        await Seed.ItemAsync(pg, 1, "too old", TestVectors.OneHot(0), now.AddDays(-60));
        var disliked = await Seed.ItemAsync(pg, 2, "disliked", TestVectors.OneHot(0), now.AddDays(-1));
        await Seed.ItemAsync(pg, 3, "old only", TestVectors.OneHot(0), now.AddDays(-90));
        await items.SetScoresAsync([new Scored(liked, 1, null), new Scored(shown, 1, null), new Scored(disliked, 1, null)], default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, item_ids) values ('2026-10-01', 'sent', @ids), ('2026-10-02', 'sent', @ids)",
                new { ids = new[] { liked, shown } });
        }

        await new FeedbackStore(pg.Db).SetVoteAsync(liked, 1, default);
        await new FeedbackStore(pg.Db).SetVoteAsync(disliked, -1, default);

        var stats = (await items.SourceStatsAsync(new DateTimeOffset(now.AddDays(-30), TimeSpan.Zero), default)).ToDictionary(s => s.FeedId);

        Assert.Equal((3, 2, 2, 1, 0), (stats[1].Items, stats[1].Candidates, stats[1].Shown, stats[1].Up, stats[1].Down));
        Assert.Equal((1, 1, 0, 0, 1), (stats[2].Items, stats[2].Candidates, stats[2].Shown, stats[2].Up, stats[2].Down));
        Assert.Equal((0, 0, 0, 0, 0), (stats[3].Items, stats[3].Candidates, stats[3].Shown, stats[3].Up, stats[3].Down));
    }
}
