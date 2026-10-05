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
