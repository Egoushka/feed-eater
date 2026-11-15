using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class RankerIdTests
{
    private static Profile P(string key, string text) => new() { Key = key, Kind = "project", Description = text };

    [Fact]
    public void Same_inputs_give_the_same_id_whatever_the_profile_order() =>
        Assert.Equal(RankerId.Compute(new WeightsOptions(), false, [P("a", "x"), P("b", "y")]), RankerId.Compute(new WeightsOptions(), false, [P("b", "y"), P("a", "x")]));

    [Fact]
    public void A_changed_weight_learned_switch_or_profile_text_changes_the_id()
    {
        var baseId = RankerId.Compute(new WeightsOptions(), false, [P("a", "x")]);

        Assert.NotEqual(baseId, RankerId.Compute(new WeightsOptions { Taste = 0.6 }, false, [P("a", "x")]));
        Assert.NotEqual(baseId, RankerId.Compute(new WeightsOptions(), true, [P("a", "x")]));
        Assert.NotEqual(baseId, RankerId.Compute(new WeightsOptions(), false, [P("a", "changed")]));
    }
}

public sealed class RankerReportTests
{
    [Fact]
    public void Lists_rankers_in_time_order_and_marks_few_votes()
    {
        var t = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var text = RankerReport.Render([new RankerRow("bbbb", t.AddDays(7), 8, 2, 0, 0), new RankerRow("aaaa", t, 3, 0, 1, 1)]);

        Assert.True(text.IndexOf("aaaa", StringComparison.Ordinal) < text.IndexOf("bbbb", StringComparison.Ordinal));
        Assert.Contains("| bbbb | 2026-11-08 | 10 | 80% | 0 | - |", text);
        Assert.Contains("| aaaa | 2026-11-01 | 3 | 100% (too few) | 2 | 50% |", text);
    }

    [Fact]
    public void Says_so_when_there_is_nothing() => Assert.Contains("No votes", RankerReport.Render([]));
}

[Collection(PostgresCollection.Name)]
public sealed class RankerStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_vote_counts_under_the_ranker_that_scored_the_item()
    {
        var now = DateTime.UtcNow;
        var a = await Seed.ItemAsync(pg, 1, "a", TestVectors.OneHot(0), now);
        var b = await Seed.ItemAsync(pg, 1, "b", TestVectors.OneHot(0), now);
        var items = new ItemStore(pg.Db);
        await items.SetScoresAsync([new Scored(a, 1, null)], default, "r1");
        await items.SetScoresAsync([new Scored(b, 1, null)], default, "r2");
        var feedback = new FeedbackStore(pg.Db);
        await feedback.SetVoteAsync(a, 1, default);
        await feedback.SetVoteAsync(b, -1, default);

        var rows = (await feedback.RankerRowsAsync(default)).ToDictionary(r => r.Ranker);

        Assert.Equal((1, 0), (rows["r1"].Up, rows["r1"].Down));
        Assert.Equal((0, 1), (rows["r2"].Up, rows["r2"].Down));
    }
}
