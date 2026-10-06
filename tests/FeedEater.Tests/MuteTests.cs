using Dapper;
using FeedEater.Digest;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MuteTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTime Now = DateTime.UtcNow;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<long> ItemAsync(long feed, string title, double hoursAgo, float[]? vector = null) =>
        Seed.ItemAsync(pg, feed, title, vector ?? TestVectors.OneHot(1), Now.AddHours(-hoursAgo));

    [Fact]
    public async Task Muting_flips_the_flag_and_reports_an_unknown_feed()
    {
        var items = new ItemStore(pg.Db);
        await ItemAsync(1, "x", 1);

        Assert.True(await items.SetFeedMutedAsync(1, true, default));
        Assert.True((await items.FeedsAsync(default)).Single().Muted);
        Assert.True(await items.SetFeedMutedAsync(1, false, default));
        Assert.False((await items.FeedsAsync(default)).Single().Muted);
        Assert.False(await items.SetFeedMutedAsync(999, true, default));
    }

    [Fact]
    public async Task A_new_entry_from_a_muted_feed_keeps_it_muted()
    {
        var items = new ItemStore(pg.Db);
        await ItemAsync(1, "x", 1);
        await items.SetFeedMutedAsync(1, true, default);

        await items.UpsertFeedAsync(new Feed(1, "Renamed", "Cat", null), default);

        var feed = (await items.FeedsAsync(default)).Single();
        Assert.True(feed.Muted);
        Assert.Equal("Renamed", feed.Title);
    }

    [Fact]
    public async Task Muted_feeds_are_out_of_candidates_and_a_cluster_falls_back_to_an_unmuted_member()
    {
        var items = new ItemStore(pg.Db);
        var mutedItem = await ItemAsync(1, "Muted feed story", 10);
        var open = await ItemAsync(2, "Open feed story", 8, TestVectors.Blend(1, 5, 0.3f));
        var alone = await ItemAsync(1, "Muted alone", 6, TestVectors.OneHot(9));
        var fine = await ItemAsync(3, "Fine", 5, TestVectors.OneHot(11));
        await items.SetFeedMutedAsync(1, true, default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set cluster_of = @mutedItem, clustered = true where id = @open", new { mutedItem, open });
        }

        var candidates = await items.CandidatesAsync(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddHours(-1), default);

        Assert.Equal([open, fine], candidates.Select(c => c.Id).Order());
        Assert.DoesNotContain(candidates, c => c.Id == alone);
    }

    [Fact]
    public async Task Posts_hide_muted_feeds_unless_asked_and_cards_know_the_feed_is_muted()
    {
        var items = new ItemStore(pg.Db);
        var hidden = await ItemAsync(1, "In a muted feed", 1);
        var shown = await ItemAsync(2, "In a normal feed", 2);
        await items.SetFeedMutedAsync(1, true, default);
        var filter = new PostFilter(DateTimeOffset.UtcNow.AddDays(-7), null, null, null, null, false, false);

        Assert.Equal([shown], (await items.PostsAsync(filter, null, 10, default)).Select(p => p.Id));
        var all = await items.PostsAsync(filter with { ShowMuted = true }, null, 10, default);
        Assert.Equal([hidden, shown], all.Select(p => p.Id));
        Assert.True(all[0].FeedMuted);
        Assert.True((await items.GetAsync(hidden, default))!.FeedMuted);
    }

    [Fact]
    public async Task Search_still_finds_items_in_muted_feeds()
    {
        var items = new ItemStore(pg.Db);
        var id = await ItemAsync(1, "pgvector tuning", 1);
        await items.SetFeedMutedAsync(1, true, default);

        Assert.Equal([id], (await items.SearchAsync(null, "pgvector", null, null, null, null, 10, default)).Select(h => h.Id));
    }

    [Fact]
    public async Task Source_stats_carry_the_muted_flag_and_posts_per_week()
    {
        var items = new ItemStore(pg.Db);
        for (var i = 0; i < 6; i++)
        {
            await ItemAsync(1, $"p{i}", i + 1, TestVectors.OneHot(20 + i));
        }

        await items.SetFeedMutedAsync(1, true, default);

        var stats = Assert.Single(await items.SourceStatsAsync(DateTimeOffset.UtcNow.AddDays(-30), default));

        Assert.True(stats.Muted);
        Assert.Equal(6, stats.Items);
        Assert.Equal(1.4, stats.PostsPerWeek, 6);
    }

    [Fact]
    public void The_brief_leaves_out_items_from_muted_feeds()
    {
        var lines = DigestBrief.Build(
        [
            new ItemView { Id = 1, Project = "homelab", Summary = "From a muted feed.", FeedMuted = true },
            new ItemView { Id = 2, Project = "homelab", Summary = "From a normal feed." },
        ]);

        Assert.Equal(("homelab", 1, "From a normal feed.", 2L), (lines[0].Project, lines[0].Count, lines[0].Text, lines[0].ItemId));
    }
}
