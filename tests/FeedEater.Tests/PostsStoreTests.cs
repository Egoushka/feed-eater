using Dapper;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostsStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTime Base = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Since = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static PostFilter Filter(string? category = null, long? feed = null, string? project = null, string? kind = null, bool unrated = false, bool summary = false) =>
        new(Since, category, feed, project, kind, unrated, summary);

    private Task<long> ItemAsync(long feed, string title, int hoursAgo, string content = "") =>
        Seed.ItemAsync(pg, feed, title, TestVectors.OneHot(1), Base.AddHours(-hoursAgo), content: content);

    private async Task CategoryAsync(long feed, string category)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update feeds set category = @category where id = @feed", new { feed, category });
    }

    [Fact]
    public async Task Pages_newest_first_with_a_keyset_that_survives_equal_timestamps()
    {
        var items = new ItemStore(pg.Db);
        var ids = new List<long>();
        foreach (var (title, hours) in new[] { ("a", 1), ("b", 2), ("c", 2), ("d", 2), ("e", 3) })
        {
            ids.Add(await ItemAsync(1, title, hours));
        }

        var seen = new List<long>();
        PageCursor? cursor = null;
        while (true)
        {
            var page = await items.PostsAsync(Filter(), cursor, 2, default);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Select(p => p.Id));
            cursor = PageCursor.Of(page[^1].PublishedAt, page[^1].Id);
        }

        Assert.Equal([ids[0], ids[3], ids[2], ids[1], ids[4]], seen);   // ties on b, c, d fall back to id descending
    }

    [Fact]
    public async Task The_cursor_round_trips_and_rejects_garbage()
    {
        var cursor = PageCursor.Of(Base, 42);

        Assert.Equal(cursor, PageCursor.Parse(cursor.ToString()));
        Assert.Null(PageCursor.Parse("nonsense"));
        Assert.Null(PageCursor.Parse("1-2-3"));
        Assert.Null(PageCursor.Parse("-5-1"));
        Assert.Null(PageCursor.Parse(null));
    }

    [Fact]
    public async Task Filters_by_window_duplicates_category_feed_project_kind_unrated_and_summary()
    {
        var items = new ItemStore(pg.Db);
        var pgPost = await ItemAsync(1, "pg post", 1);
        var agentPost = await ItemAsync(2, "agent post", 2);
        var unread = await ItemAsync(2, "no analysis", 3);
        var old = await Seed.ItemAsync(pg, 1, "too old", TestVectors.OneHot(1), Base.AddDays(-60));
        var dup = await ItemAsync(1, "duplicate", 1);
        await CategoryAsync(1, "Postgres");
        await CategoryAsync(2, "Agents & MCP");
        await Seed.ReadAsync(pg, pgPost, "homelab", "improve");
        await Seed.ReadAsync(pg, agentPost, "chargehand", "fyi");
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set duplicate_of = @pgPost where id = @dup", new { pgPost, dup });
        }

        await new ItemStore(pg.Db).SetScoresAsync([new Scored(unread, 1, "chargehand")], default);
        await new FeedbackStore(pg.Db).SetVoteAsync(agentPost, 1, default);

        async Task<long[]> Ids(PostFilter f) => (await items.PostsAsync(f, null, 50, default)).Select(p => p.Id).ToArray();

        Assert.Equal([pgPost, agentPost, unread], await Ids(Filter()));
        Assert.Equal([pgPost], await Ids(Filter(category: "Postgres")));
        Assert.Equal([agentPost, unread], await Ids(Filter(feed: 2)));
        Assert.Equal([agentPost, unread], await Ids(Filter(project: "chargehand")));   // by read project or by profile key
        Assert.Equal([pgPost], await Ids(Filter(kind: "improve")));
        Assert.Equal([pgPost, unread], await Ids(Filter(unrated: true)));
        Assert.Equal([pgPost, agentPost], await Ids(Filter(summary: true)));
        Assert.DoesNotContain(old, await Ids(Filter()));
        Assert.Contains(old, await Ids(Filter() with { Since = Base.AddDays(-90) }));
    }

    [Fact]
    public async Task Cards_carry_the_category_the_vote_the_summary_and_a_clipped_excerpt()
    {
        var items = new ItemStore(pg.Db);
        var id = await ItemAsync(1, "post", 1, new string('x', 5000));
        await CategoryAsync(1, "Postgres");
        await Seed.ReadAsync(pg, id, "homelab", "improve");
        await new FeedbackStore(pg.Db).SetVoteAsync(id, -1, default);

        var post = Assert.Single(await items.PostsAsync(Filter(), null, 5, default));

        Assert.Equal("Postgres", post.Category);
        Assert.Equal(-1, post.Vote);
        Assert.Equal("s", post.Summary);
        Assert.Equal(600, post.Content.Length);
        Assert.Equal("homelab", post.Project);
    }

    [Fact]
    public async Task Rated_lists_votes_and_ideas_newest_first_with_a_cursor()
    {
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var a = await ItemAsync(1, "a", 1);
        var b = await ItemAsync(1, "b", 2);
        var c = await ItemAsync(1, "c", 3);
        await feedback.SetVoteAsync(a, 1, default);
        await feedback.SetVoteAsync(b, 1, default);
        await feedback.SetVoteAsync(c, -1, default);
        await feedback.AddIdeaAsync(new Idea { ItemId = b, PlaneProject = "LAB", PlaneIssueId = "x", Title = "t", At = DateTime.UtcNow }, default);
        await using (var conn = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("update votes set at = @at where item_id = @a", new { a, at = DateTime.UtcNow.AddHours(-5) });
        }

        var up = await items.RatedAsync("up", null, 10, default);
        var down = await items.RatedAsync("down", null, 10, default);
        var ideas = await items.RatedAsync("idea", null, 10, default);
        var afterFirst = await items.RatedAsync("up", PageCursor.Of(up[0].RatedAt, up[0].Id), 10, default);

        Assert.Equal([b, a], up.Select(i => i.Id));
        Assert.Equal([c], down.Select(i => i.Id));
        Assert.Equal([b], ideas.Select(i => i.Id));
        Assert.Equal("LAB", ideas[0].FiledIn);
        Assert.Equal(1, ideas[0].Vote);
        Assert.Equal([a], afterFirst.Select(i => i.Id));
    }

    [Fact]
    public async Task A_vote_can_be_cleared_and_totals_count_votes_and_ideas()
    {
        var feedback = new FeedbackStore(pg.Db);
        var a = await ItemAsync(1, "a", 1);
        var b = await ItemAsync(1, "b", 2);
        await feedback.SetVoteAsync(a, 1, default);
        await feedback.SetVoteAsync(b, -1, default);
        await feedback.AddIdeaAsync(new Idea { ItemId = a, PlaneProject = "LAB", PlaneIssueId = "x", Title = "t", At = DateTime.UtcNow }, default);

        var before = await feedback.TotalsAsync(default);
        Assert.Equal((1, 1, 1), (before.Up, before.Down, before.Ideas));

        await feedback.ClearVoteAsync(b, default);
        await feedback.ClearVoteAsync(9999, default);

        var totals = await feedback.TotalsAsync(default);
        Assert.Equal((1, 0, 1), (totals.Up, totals.Down, totals.Ideas));
        Assert.Null((await new ItemStore(pg.Db).GetAsync(b, default))!.Vote);
    }

    [Fact]
    public async Task Categories_and_feeds_feed_the_filter_lists()
    {
        var items = new ItemStore(pg.Db);
        await ItemAsync(1, "x", 1);
        await ItemAsync(2, "y", 1);
        await ItemAsync(3, "z", 1);
        await CategoryAsync(1, "Postgres");
        await CategoryAsync(2, "Postgres");
        await CategoryAsync(3, "Security");

        Assert.Equal(["Postgres", "Security"], await items.CategoriesAsync(default));
        Assert.Equal(["Feed 1", "Feed 2", "Feed 3"], (await items.FeedsAsync(default)).Select(f => f.Title));
    }
}
