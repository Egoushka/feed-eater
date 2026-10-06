using Dapper;
using FeedEater.Hype;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutopsyStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 7, 10, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<long> SnapshotAsync(string title, double ageDays, long feed = 1, float[]? embedding = null, DateTime? publishedAt = null)
    {
        var id = await Seed.ItemAsync(pg, feed, title, embedding ?? TestVectors.OneHot(1), publishedAt ?? Now.UtcDateTime.AddDays(-ageDays - 1));
        await new AutopsyStore(pg.Db).AddAsync(new RepoSnapshot(id, "acme/widget", Now.AddDays(-ageDays), 100, Now.AddDays(-ageDays - 2), "v1", Now.AddYears(-1)), default);
        return id;
    }

    private Task<IReadOnlyList<AutopsyCandidate>> PendingAsync(string month = "2026-11-01") => new AutopsyStore(pg.Db).PendingAsync(month, Now, 0.84, default);

    [Fact]
    public async Task A_snapshot_is_ripe_from_87_days_and_expires_after_180()
    {
        var young = await SnapshotAsync("86 days", 86);
        var ripe = await SnapshotAsync("87 days", 87);
        var old = await SnapshotAsync("179 days", 179);
        var expired = await SnapshotAsync("181 days", 181);

        var pending = await PendingAsync();

        Assert.Equal([old, ripe], pending.Select(c => c.Snapshot.ItemId));   // oldest first
        Assert.DoesNotContain(pending, c => c.Snapshot.ItemId == young || c.Snapshot.ItemId == expired);
    }

    [Fact]
    public async Task A_snapshot_scored_by_an_earlier_month_is_not_taken_again_but_this_months_come_back()
    {
        var earlier = await SnapshotAsync("Earlier", 100);
        var thisMonth = await SnapshotAsync("This month", 100);
        var fresh = await SnapshotAsync("Fresh", 100);
        var store = new AutopsyStore(pg.Db);
        await store.MarkScoredAsync([earlier], "2026-10-01", default);
        await store.MarkScoredAsync([thisMonth], "2026-11-01", default);

        var pending = await PendingAsync();

        Assert.Equal([thisMonth, fresh], pending.Select(c => c.Snapshot.ItemId));
    }

    [Fact]
    public async Task The_candidate_carries_the_snapshot_the_item_the_feed_the_relevance_the_idea_and_the_saved_flag()
    {
        var plain = await SnapshotAsync("Plain", 90);
        var rich = await SnapshotAsync("Rich", 91, feed: 2);
        await SqlAsync("insert into triage (item_id, relevance, kind, model) values (@rich, 3, 'new', 'm')", new { rich });
        await SqlAsync("insert into ideas (item_id, plane_project, plane_issue_id, title) values (@rich, 'LAB', 'x', 'Try it')", new { rich });
        await SqlAsync("insert into saved (item_id, karakeep_id) values (@rich, 'k')", new { rich });

        var pending = await PendingAsync();
        var p = pending.Single(c => c.Snapshot.ItemId == plain);
        var r = pending.Single(c => c.Snapshot.ItemId == rich);

        Assert.Equal(("Plain", "Feed 1", null, IdeaOutcome.None, false), (p.Title, p.Feed, p.Relevance, p.Idea, p.Saved));
        Assert.Equal(("Rich", "Feed 2", 3, IdeaOutcome.Filed, true), (r.Title, r.Feed, r.Relevance, r.Idea, r.Saved));
        Assert.Equal(("acme/widget", 100, "v1"), (r.Snapshot.Repo, r.Snapshot.Stars, r.Snapshot.ReleaseTag));
        Assert.Equal(Now.AddDays(-91), r.Snapshot.TakenAt);
    }

    [Fact]
    public async Task Related_later_counts_other_feeds_after_the_item_in_its_cluster_or_close_in_meaning()
    {
        var published = Now.UtcDateTime.AddDays(-95);
        var head = await Seed.ItemAsync(pg, 4, "Head of the story", TestVectors.OneHot(7), published.AddDays(-1));
        var item = await SnapshotAsync("The liked item", 90, feed: 1, publishedAt: published);
        await SqlAsync("update items set cluster_of = @head where id = @item", new { head, item });
        var similar = await Seed.ItemAsync(pg, 2, "Similar, other feed, later", TestVectors.Blend(1, 2, 0.3f), published.AddDays(2));
        var member = await Seed.ItemAsync(pg, 3, "Same story, unrelated vector", TestVectors.OneHot(5), published.AddDays(3));
        await SqlAsync("update items set cluster_of = @head where id = @member", new { head, member });
        await Seed.ItemAsync(pg, 1, "Similar but the same feed", TestVectors.Blend(1, 2, 0.3f), published.AddDays(2));
        await Seed.ItemAsync(pg, 2, "Similar but earlier", TestVectors.Blend(1, 2, 0.3f), published.AddDays(-2));
        await Seed.ItemAsync(pg, 3, "Unrelated, later", TestVectors.OneHot(8), published.AddDays(2));
        await Seed.ItemAsync(pg, 3, "Not similar enough", TestVectors.Blend(1, 2, 0.6f), published.AddDays(2));
        var duplicate = await Seed.ItemAsync(pg, 2, "A duplicate", TestVectors.Blend(1, 2, 0.3f), published.AddDays(2));
        await SqlAsync("update items set duplicate_of = @similar where id = @duplicate", new { similar, duplicate });

        var candidate = Assert.Single(await PendingAsync(), c => c.Snapshot.ItemId == item);

        Assert.Equal(2, candidate.RelatedLater);   // the similar one and the cluster member
    }

    [Fact]
    public async Task An_item_without_an_embedding_matches_only_through_its_cluster()
    {
        var id = await SnapshotAsync("No vector", 90);
        await SqlAsync("update items set embedding = null where id = @id", new { id });
        await Seed.ItemAsync(pg, 2, "Similar", TestVectors.OneHot(1), Now.UtcDateTime.AddDays(-50));

        Assert.Equal(0, Assert.Single(await PendingAsync()).RelatedLater);
    }

    private static AutopsyReport Report(string title = "t") => AutopsyScorer.Build(
        [new AutopsyItem(1, title, "https://e.example/", "Feed", "acme/widget", 3, RepoVerdict.Grew, 10, 15, 50, true, true, IdeaOutcome.Filed, true, 2)], 1, 2, 3, Now);

    [Fact]
    public async Task An_autopsy_round_trips_and_saving_the_month_again_replaces_it_and_clears_sent()
    {
        var store = new AutopsyStore(pg.Db);
        var report = Report();

        await store.SaveAsync("2026-11-01", Now, report, "<b>hi</b>", default);
        await store.MarkSentAsync("2026-11-01", Now.AddMinutes(1), default);
        var row = (await store.GetAsync("2026-11-01", default))!;

        Assert.Equal(report.Total, row.Report.Total);
        Assert.Equal((1, 2, 3), (row.Report.NoRepo, row.Report.NoBaseline, row.Report.Unchecked));
        var item = row.Report.Items.Single();
        Assert.Equal((RepoVerdict.Grew, IdeaOutcome.Filed, 3, 50.0), (item.Verdict, item.Idea, item.Relevance, item.StarsGrowthPercent));
        Assert.Equal(report.ByFeed.Single().Label, row.Report.ByFeed.Single().Label);
        Assert.Equal(Now.AddMinutes(1), row.SentAt);
        Assert.Equal("<b>hi</b>", row.Message);

        await store.SaveAsync("2026-11-01", Now, report, "<b>again</b>", default);
        await store.SaveAsync("2026-10-01", Now.AddDays(-31), report, "old", default);

        Assert.Null((await store.GetAsync("2026-11-01", default))!.SentAt);
        Assert.Equal("2026-11-01", (await store.LatestAsync(default))!.Month);
        Assert.Equal(["2026-11-01", "2026-10-01"], await store.ListAsync(10, default));
        Assert.Null(await store.GetAsync("2001-01-01", default));
    }

    [Fact]
    public async Task The_stored_report_keeps_the_verdict_as_a_word()
    {
        await new AutopsyStore(pg.Db).SaveAsync("2026-11-01", Now, Report(), "m", default);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();

        var verdict = await c.ExecuteScalarAsync<string>("select report -> 'items' -> 0 ->> 'verdict' from autopsy");

        Assert.Equal("Grew", verdict);
    }

    [Fact]
    public async Task A_snapshot_is_taken_once_per_item()
    {
        var id = await SnapshotAsync("Once", 90);
        var store = new AutopsyStore(pg.Db);

        await store.AddAsync(new RepoSnapshot(id, "acme/other", Now, 999, null, null, null), default);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal("acme/widget", await c.ExecuteScalarAsync<string>("select repo from repo_snapshots where item_id = @id", new { id }));
        Assert.Equal(100, await c.ExecuteScalarAsync<int>("select stars from repo_snapshots where item_id = @id", new { id }));
    }

    [Fact]
    public async Task Candidates_are_liked_items_without_a_snapshot_oldest_vote_first()
    {
        var feedback = new FeedbackStore(pg.Db);
        var first = await Seed.ItemAsync(pg, 1, "First", TestVectors.OneHot(1), content: "c");
        var second = await Seed.ItemAsync(pg, 1, "Second", TestVectors.OneHot(2));
        var done = await Seed.ItemAsync(pg, 1, "Done", TestVectors.OneHot(3));
        var down = await Seed.ItemAsync(pg, 1, "Down", TestVectors.OneHot(4));
        await Seed.ItemAsync(pg, 1, "Unvoted", TestVectors.OneHot(5));
        await feedback.SetVoteAsync(second, 1, default);
        await feedback.SetVoteAsync(first, 1, default);
        await feedback.SetVoteAsync(done, 1, default);
        await feedback.SetVoteAsync(down, -1, default);
        var store = new AutopsyStore(pg.Db);
        await store.AddAsync(new RepoSnapshot(done, null, Now, null, null, null, null), default);

        var candidates = await store.CandidatesAsync(10, default);

        Assert.Equal([second, first], candidates.Select(c => c.ItemId));
        Assert.Equal("c", candidates[1].Content);
        Assert.Single(await store.CandidatesAsync(1, default));
    }
}
