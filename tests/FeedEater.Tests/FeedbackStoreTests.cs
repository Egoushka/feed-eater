using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedbackStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_last_vote_wins_and_counts_by_time()
    {
        var store = new FeedbackStore(pg.Db);
        var id = await Seed.ItemAsync(pg, 1, "a", TestVectors.OneHot(0));

        await store.SetVoteAsync(id, 1, default);
        await store.SetVoteAsync(id, -1, default);

        var counts = await store.VotesSinceAsync(DateTimeOffset.UtcNow.AddMinutes(-1), default);
        Assert.Equal(0, counts.Up);
        Assert.Equal(1, counts.Down);
    }

    [Fact]
    public async Task A_plain_vote_has_weight_one_and_a_later_vote_replaces_the_weight_too()
    {
        var store = new FeedbackStore(pg.Db);
        var id = await Seed.ItemAsync(pg, 1, "a", TestVectors.OneHot(0));

        await store.SetVoteAsync(id, 1, default);
        Assert.Equal(1, (await store.LabeledVectorsAsync(default)).Single().Weight);

        await store.SetVoteAsync(id, 1, default, weight: 3);
        Assert.Equal(3, (await store.LabeledVectorsAsync(default)).Single().Weight);

        await store.SetVoteAsync(id, -1, default);
        var vote = (await store.LabeledVectorsAsync(default)).Single();
        Assert.False(vote.Liked);
        Assert.Equal(1, vote.Weight);
    }

    [Fact]
    public async Task Weights_reach_the_vectors_and_the_feed_sums_while_vote_counts_stay_rows()
    {
        var store = new FeedbackStore(pg.Db);
        var up = await Seed.ItemAsync(pg, 1, "up", TestVectors.OneHot(1));
        var up2 = await Seed.ItemAsync(pg, 1, "up2", TestVectors.OneHot(2));
        var down = await Seed.ItemAsync(pg, 1, "down", TestVectors.OneHot(3));
        var filed = await Seed.ItemAsync(pg, 1, "filed", TestVectors.OneHot(4));
        var filedAndWeak = await Seed.ItemAsync(pg, 1, "filed and weak", TestVectors.OneHot(5));
        await Seed.SignalAsync(pg, "k1", TestVectors.OneHot(6));
        await store.SetVoteAsync(up, 1, default, weight: 3);
        await store.SetVoteAsync(up2, 1, default);
        await store.SetVoteAsync(down, -1, default, weight: 0.5);
        await store.SetVoteAsync(filedAndWeak, 1, default, weight: 0.5);
        foreach (var item in new[] { filed, filedAndWeak })
        {
            await store.AddIdeaAsync(new Idea { ItemId = item, PlaneProject = "FEED", PlaneIssueId = "i", Title = "t", At = DateTime.UtcNow }, default);
        }

        var positives = (await store.PositiveWeightedAsync(500, default)).ToDictionary(v => Array.IndexOf(v.X, 1f), v => v.Weight);
        var negatives = await store.NegativeWeightedAsync(500, default);
        var feed = (await store.FeedVotesAsync(default)).Single();
        var counts = await store.VotesSinceAsync(DateTimeOffset.UtcNow.AddMinutes(-1), default);
        var totals = await store.TotalsAsync(default);

        Assert.Equal(new Dictionary<int, double> { [1] = 3, [2] = 1, [4] = 1, [5] = 0.5, [6] = 1 }, positives);
        Assert.Equal(0.5, negatives.Single().Weight);
        Assert.Equal((4.5, 0.5), (feed.Up, feed.Down));
        Assert.Equal((3, 1), (counts.Up, counts.Down));
        Assert.Equal((3, 1), (totals.Up, totals.Down));
    }

    [Fact]
    public async Task Positives_come_from_up_votes_ideas_and_signals_and_negatives_from_down_votes()
    {
        var store = new FeedbackStore(pg.Db);
        var up = await Seed.ItemAsync(pg, 1, "up", TestVectors.OneHot(1));
        var down = await Seed.ItemAsync(pg, 1, "down", TestVectors.OneHot(2));
        var filed = await Seed.ItemAsync(pg, 2, "filed", TestVectors.OneHot(3));
        await Seed.SignalAsync(pg, "k1", TestVectors.OneHot(4));
        await store.SetVoteAsync(up, 1, default);
        await store.SetVoteAsync(down, -1, default);
        await store.AddIdeaAsync(new Idea { ItemId = filed, PlaneProject = "FEED", PlaneIssueId = "i1", Title = "t", At = DateTime.UtcNow }, default);

        var positives = await store.PositiveVectorsAsync(500, default);
        var negatives = await store.NegativeVectorsAsync(500, default);
        var feeds = await store.FeedVotesAsync(default);

        Assert.Equal(new[] { 1, 3, 4 }, positives.Select(v => Array.IndexOf(v, 1f)).Order());
        Assert.Equal(2, Array.IndexOf(negatives.Single(), 1f));
        var feed1 = feeds.Single(f => f.FeedId == 1);
        Assert.Equal((1, 1), (feed1.Up, feed1.Down));
    }

    [Fact]
    public async Task An_item_that_was_voted_up_and_filed_counts_once_as_a_positive()
    {
        var store = new FeedbackStore(pg.Db);
        var id = await Seed.ItemAsync(pg, 1, "both", TestVectors.OneHot(1));
        await store.SetVoteAsync(id, 1, default);
        await store.AddIdeaAsync(new Idea { ItemId = id, PlaneProject = "FEED", PlaneIssueId = "i1", Title = "t", At = DateTime.UtcNow }, default);

        var positives = await store.PositiveVectorsAsync(500, default);

        Assert.Single(positives);
    }

    [Fact]
    public async Task An_idea_is_stored_once_and_listed_by_project()
    {
        var store = new FeedbackStore(pg.Db);
        var a = await Seed.ItemAsync(pg, 1, "a", TestVectors.OneHot(0));
        var b = await Seed.ItemAsync(pg, 1, "b", TestVectors.OneHot(1));

        await store.AddIdeaAsync(new Idea { ItemId = a, PlaneProject = "SKAR", PlaneIssueId = "first", Title = "x", At = DateTime.UtcNow }, default);
        await store.AddIdeaAsync(new Idea { ItemId = a, PlaneProject = "SKAR", PlaneIssueId = "second", Title = "x", At = DateTime.UtcNow }, default);
        await store.AddIdeaAsync(new Idea { ItemId = b, PlaneProject = "FEED", PlaneIssueId = "third", Title = "y", At = DateTime.UtcNow }, default);

        Assert.Equal("first", (await store.GetIdeaAsync(a, default))!.PlaneIssueId);
        Assert.Equal(["third"], (await store.IdeasAsync("FEED", 10, default)).Select(i => i.PlaneIssueId));
        Assert.Equal(2, (await store.IdeasAsync(null, 10, default)).Count);
    }
}
