using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ClusterTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly float[] Story = TestVectors.OneHot(0);
    private static readonly float[] SameStory = TestVectors.Blend(0, 1, 0.3f);   // cosine 0.92 to Story
    private static readonly float[] Related = TestVectors.Blend(0, 2, 0.5f);     // cosine 0.71 to Story

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private StoryClusterer Clusterer() => new(new ClusterStore(pg.Db), Options.Create(new FeedEaterOptions()), NullLogger<StoryClusterer>.Instance);

    private Task<long> ItemAsync(long feed, string title, float[] vector, double hoursAgo) =>
        Seed.ItemAsync(pg, feed, title, vector, Now.AddHours(-hoursAgo));

    private async Task<Dictionary<long, long?>> LinksAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<(long Id, long? Head)>("select id, cluster_of from items")).ToDictionary(r => r.Id, r => r.Head);
    }

    [Theory]
    [InlineData("Postgres 19 released", "Postgres 19 is out", true)]
    [InlineData("pgvector v0.8.1", "pgvector v0.8.2", false)]
    [InlineData("pgvector v0.8.1 released", "Release notes: pgvector 0.8.1", true)]
    [InlineData("Postgres 18.1 and 17.7", "Postgres 18.1 and 17.7 released", true)]
    [InlineData("Postgres 18.1 and 17.7", "Postgres 18.2 and 17.8", false)]
    [InlineData("Redis 8.2.0-rc1 is here", "Redis 8.2.0", false)]
    [InlineData("pgvector v0.8.1", "Why vector search matters", true)]
    public void Titles_naming_different_versions_are_not_one_story(string a, string b, bool same) =>
        Assert.Equal(same, StoryClusterer.SameStory(a, b));

    [Fact]
    public async Task Links_the_same_story_from_another_feed_to_the_first_and_leaves_the_rest_alone()
    {
        var hn = await ItemAsync(1, "Occam raises 3.8M", Story, 10);
        var blog = await ItemAsync(2, "Occam Industries raised 3.8M for autonomy", SameStory, 8);
        var reddit = await ItemAsync(3, "Occam round discussed", SameStory, 6);
        var other = await ItemAsync(4, "Something else", Related, 5);
        var sameFeed = await ItemAsync(1, "Occam follow-up from the same feed", SameStory, 4);

        Assert.Equal(2, await Clusterer().RunAsync(default));

        var links = await LinksAsync();
        Assert.Equal(hn, links[blog]);
        Assert.Equal(hn, links[reddit]);   // attaches to the head, never to a member
        Assert.Null(links[hn]);
        Assert.Null(links[other]);
        Assert.Null(links[sameFeed]);   // one feed repeating itself is not a cross-feed story; it still becomes its own head
    }

    [Fact]
    public async Task Respects_the_window_the_version_guard_duplicates_and_runs_once()
    {
        var old = await ItemAsync(1, "Tool v1.2.0 released", Story, 24 * 5);
        var near = await ItemAsync(2, "Tool v1.2.1 released", SameStory, 1);
        var far = await ItemAsync(3, "Tool v1.2.0 story", SameStory, 0.5);
        var dup = await ItemAsync(4, "Tool v1.2.1 story again", SameStory, 0.4);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set duplicate_of = @old where id = @dup", new { old, dup });
        }

        await Clusterer().RunAsync(default);
        var links = await LinksAsync();

        Assert.Null(links[near]);   // different version
        Assert.Null(links[far]);    // the only similar items are 5 days away or a different version
        Assert.Null(links[dup]);    // hard duplicates are skipped
        Assert.Equal(0, await Clusterer().RunAsync(default));
        await using var conn = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("select count(*) from items where clustered"));   // the duplicate stays unconsidered
    }

    [Fact]
    public async Task A_late_ingested_older_item_joins_an_existing_head()
    {
        var head = await ItemAsync(1, "Story", Story, 1);
        await Clusterer().RunAsync(default);
        var older = await ItemAsync(2, "Story elsewhere", SameStory, 20);

        await Clusterer().RunAsync(default);

        Assert.Equal(head, (await LinksAsync())[older]);
    }

    [Fact]
    public async Task Candidates_take_the_earliest_item_of_each_story_and_skip_stories_already_triaged_on_an_earlier_day()
    {
        var items = new ItemStore(pg.Db);
        var first = await ItemAsync(1, "Story first", Story, 10);
        var second = await ItemAsync(2, "Story second", SameStory, 8);
        var solo = await ItemAsync(3, "Solo", Related, 6);
        var oldStory = await ItemAsync(1, "Yesterday's story", TestVectors.OneHot(7), 30);
        var oldRepeat = await ItemAsync(2, "Yesterday's story, retold today", TestVectors.Blend(7, 8, 0.3f), 2);
        await Clusterer().RunAsync(default);
        await new AnalysisStore(pg.Db).SaveTriageAsync(oldStory, new TriageResult { Relevance = 2 }, "m", default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update triage set at = now() - interval '1 day'");
        }

        var candidates = await items.CandidatesAsync(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddHours(-1), default);

        Assert.Equal([first, solo], candidates.Select(c => c.Id).Order());
        Assert.DoesNotContain(candidates, c => c.Id == second || c.Id == oldRepeat);
    }

    [Fact]
    public async Task Cards_know_how_many_other_feeds_carry_the_story_and_which()
    {
        var items = new ItemStore(pg.Db);
        var first = await ItemAsync(1, "Story first", Story, 10);
        var second = await ItemAsync(2, "Story second", SameStory, 8);
        var third = await ItemAsync(3, "Story third", SameStory, 6);
        var solo = await ItemAsync(4, "Solo", Related, 6);
        await Clusterer().RunAsync(default);
        await Seed.ReadAsync(pg, first, "homelab", "improve");

        var view = (await items.GetAsync(second, default))!;
        var post = (await items.PostsAsync(new PostFilter(DateTimeOffset.UtcNow.AddDays(-7), null, null, null, null, false, false), null, 10, default)).ToDictionary(p => p.Id);
        var enriched = await items.WithMembersAsync([post[second], post[solo]], default);
        var hits = await items.SearchAsync(null, "story", null, null, null, null, 10, default);
        var digest = await items.DigestItemsAsync([first], default);

        Assert.Equal(2, view.Also);
        Assert.Equal(2, post[first].Also);
        Assert.Equal(0, post[solo].Also);
        Assert.Equal([first, third], enriched[0].AlsoIn.Select(m => m.Id));
        Assert.Equal("Feed 1", enriched[0].AlsoIn[0].Feed);
        Assert.Empty(enriched[1].AlsoIn);
        Assert.All(hits, h => Assert.Equal(2, h.Also));
        Assert.Equal([second, third], Assert.Single(digest).AlsoIn.Select(m => m.Id));
    }

    [Fact]
    public async Task The_telegram_card_lists_the_other_feeds_with_safe_links_only()
    {
        var item = new DigestItem
        {
            Id = 1, Title = "T", Url = "https://a.example", Feed = "F", Summary = "S", Why = "W",
            AlsoIn = [new(2, "t", "https://b.example/x", "Feed B"), new(3, "t", "javascript:alert(1)", "<b>Feed C</b>"), new(4, "t", "https://d.example", "D"), new(5, "t", "https://e.example", "E")],
        };

        var html = DigestFormatter.Item(item, null, null).Html;

        Assert.Contains("Also in: <a href=\"https://b.example/x\">Feed B</a>, &lt;b&gt;Feed C&lt;/b&gt;, <a href=\"https://d.example\">D</a> +1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Also in", DigestFormatter.Item(item with { AlsoIn = [] }, null, null).Html, StringComparison.Ordinal);
    }
}
