using Dapper;
using Microsoft.Extensions.Options;
using FeedEater.Storage;
using FeedEater.Swipe;

namespace FeedEater.Tests;

public sealed class YieldLabelTests
{
    [Theory]
    [InlineData("idea", true)]
    [InlineData("save", true)]
    [InlineData("share", true)]
    [InlineData("like", false)]
    [InlineData("like,open", false)]
    [InlineData("like,share", true)]
    [InlineData("dislike", false)]
    [InlineData("save,dislike", false)]
    [InlineData("next", false)]
    [InlineData("dwell,back,next", false)]
    public void A_card_is_a_yield_example_by_its_acts(string acts, bool positive) =>
        Assert.Equal(positive, YieldLabels.Positive(acts.Split(','), null));

    [Fact]
    public void A_category_can_narrow_which_acts_are_yield_for_it()
    {
        string[] events = ["share", "like"];

        Assert.False(YieldLabels.Positive(["save"], events));
        Assert.True(YieldLabels.Positive(["share"], events));
        Assert.True(YieldLabels.Positive(["like", "share"], events));
        Assert.False(YieldLabels.Positive(["save", "dislike"], events));
    }

    [Fact]
    public void Time_on_a_card_and_moving_on_are_worth_nothing() =>
        Assert.Equal(0, YieldLabels.Score(["dwell", "next", "back"], null));
}

public sealed class SwipeQueueExploreTests
{
    private static List<QueueCandidate> Pool(int n = 100) =>
        Enumerable.Range(1, n).Select(i => new QueueCandidate(i, n - i, null, TestVectors.OneHot(i % 50))).ToList();

    [Fact]
    public void With_no_epsilon_every_card_is_exploit_and_the_best_scored()
    {
        var queue = SwipeQueue.Build(Pool(), new Dictionary<string, double>(), 20, 3, 0);

        Assert.All(queue, q => Assert.Equal(SwipeQueue.Exploit, q.Source));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), queue.Select(q => q.Id).Order());
    }

    [Fact]
    public void Explore_cards_come_from_outside_the_top_30_percent_and_nothing_repeats()
    {
        var queue = SwipeQueue.Build(Pool(), new Dictionary<string, double>(), 40, 11, 0.5);
        var explore = queue.Where(q => q.Source == SwipeQueue.Explore).ToList();

        Assert.Equal(40, queue.Count);
        Assert.Equal(queue.Count, queue.Select(q => q.Id).Distinct().Count());
        Assert.NotEmpty(explore);
        Assert.All(explore, q => Assert.True(q.Id > 30, $"{q.Id} is inside the top 30%"));
    }

    [Fact]
    public void The_explore_share_follows_epsilon_over_many_sessions()
    {
        var total = 0;
        var explore = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var queue = SwipeQueue.Build(Pool(), new Dictionary<string, double>(), 40, seed, 0.2);
            total += queue.Count;
            explore += queue.Count(q => q.Source == SwipeQueue.Explore);
        }

        Assert.InRange(explore / (double)total, 0.17, 0.23);
    }

    [Fact]
    public void An_explore_card_is_the_one_least_like_what_was_picked()
    {
        // Ten candidates: the top 3 (e0) are exploit picks; of the other 7, one is orthogonal and six repeat e0.
        var pool = new List<QueueCandidate>();
        for (var i = 1; i <= 10; i++)
        {
            pool.Add(new QueueCandidate(i, 100 - i, null, i == 6 ? TestVectors.OneHot(1) : TestVectors.OneHot(0)));
        }

        var seed = Enumerable.Range(0, 500).First(s => SwipeQueue.Build(pool, new Dictionary<string, double>(), 4, s, 0.25).Count(q => q.Source == SwipeQueue.Explore) == 1);
        var queue = SwipeQueue.Build(pool, new Dictionary<string, double>(), 4, seed, 0.25);

        Assert.Equal(6, queue.Single(q => q.Source == SwipeQueue.Explore).Id);
    }

    [Fact]
    public void The_predicted_value_travels_with_the_card()
    {
        var pool = new List<QueueCandidate> { new(1, 5, null, null, 0.8), new(2, 4, null, null, 0.1) };

        var queue = SwipeQueue.Build(pool, new Dictionary<string, double>(), 2, 1);

        Assert.Equal(0.8, queue.Single(q => q.Id == 1).Predicted);
    }

    [Theory]
    [InlineData("src/FeedEater/Swipe/SwipeYield.cs")]
    [InlineData("src/FeedEater/Swipe/SwipeQueue.cs")]
    public void The_ranking_code_never_reads_dwell(string file)
    {
        var code = File.ReadLines(RepoRoot.File(file)).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

        Assert.All(code, l => Assert.DoesNotContain("dwell", l, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_service_scores_candidates_without_reading_dwell()
    {
        var text = File.ReadAllText(RepoRoot.File("src/FeedEater/Swipe/SwipeService.cs"));
        var build = text[text.IndexOf("BuildQueueAsync", StringComparison.Ordinal)..text.IndexOf("public async Task<ActResult> ActAsync", StringComparison.Ordinal)];

        Assert.DoesNotContain("dwell", build, StringComparison.OrdinalIgnoreCase);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class SwipeYieldStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private SwipeYield Yield(int needed) => new(pg.Db, new CategoryStore(pg.Db), Options.Create(new FeedEaterOptions { Swipe = { YieldMinEvents = needed } }));

    private async Task LogAsync(long session, int position, long item, string[] acts, int dwellMs)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            "insert into swipe_events (session_id, position, kind, ref_id, acts, dwell_ms) values (@session, @position, 'item', @item, @acts, @dwellMs)",
            new { session, position, item, acts, dwellMs });
    }

    private async Task<long> SessionAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<long>("insert into swipe_sessions (card_cap, time_cap_s) values (40, 900) returning id");
    }

    /// <summary>Items about topic 0 are saved, items about topic 1 are skipped; dwell is set by <paramref name="dwell"/>.</summary>
    private async Task SeedAsync(Func<int, int> dwell)
    {
        var session = await SessionAsync();
        for (var i = 0; i < 6; i++)
        {
            var good = i % 2 == 0;
            var item = await Seed.ItemAsync(pg, 1, $"Item {i}", good ? TestVectors.Blend(0, 5 + i, 0.2f) : TestVectors.Blend(1, 5 + i, 0.2f));
            await LogAsync(session, i, item, good ? ["save"] : ["next"], dwell(i));
        }
    }

    [Fact]
    public async Task The_model_waits_for_enough_good_and_other_examples()
    {
        await SeedAsync(_ => 1000);

        Assert.Null(await Yield(4).ModelAsync(default));
        Assert.NotNull(await Yield(3).ModelAsync(default));
        var status = await Yield(4).StatusAsync(default);
        Assert.Equal((3, 3, false), (status.Positives, status.Negatives, status.Ready));
    }

    [Fact]
    public async Task A_sent_seed_and_a_remembered_card_count_as_good_examples_and_a_first_answer_does_not()
    {
        var sent = await Seed.ItemAsync(pg, 1, "Sent", TestVectors.OneHot(0));
        var remembered = await Seed.ItemAsync(pg, 1, "Remembered", TestVectors.OneHot(1));
        var firstAnswer = await Seed.ItemAsync(pg, 1, "First", TestVectors.OneHot(2));
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into post_seeds (item_id, angle, status) values (@sent, 'a', 'sent')", new { sent });
            foreach (var (item, step) in new[] { (remembered, 2), (firstAnswer, 0) })
            {
                var card = await c.ExecuteScalarAsync<long>(
                    "insert into recall_cards (source, ref, item_id, front, back, due_at) values ('item', 'item:' || @item, @item, 'q', 'a', now()) returning id", new { item });
                await c.ExecuteAsync("insert into recall_reviews (card_id, result, step_before) values (@card, 'good', @step)", new { card, step });
            }
        }

        var data = await Yield(1).LabeledAsync(default);

        Assert.Equal(2, data.Count);   // the first answer is not yet proof of memory, so it is no example at all
        Assert.All(data, d => Assert.True(d.Liked));
    }

    [Fact]
    public async Task The_model_learns_what_was_saved()
    {
        await SeedAsync(_ => 1000);

        var model = (await Yield(3).ModelAsync(default))!;

        Assert.True(model.Probability(TestVectors.OneHot(0)) > model.Probability(TestVectors.OneHot(1)));
    }

    [Fact]
    public async Task Changing_how_long_each_card_was_on_screen_changes_nothing()
    {
        await SeedAsync(i => 500 + (i * 10));
        var before = (await Yield(3).ModelAsync(default))!;
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update swipe_events set dwell_ms = 3600000 - dwell_ms * 100");
        }

        var after = (await Yield(3).ModelAsync(default))!;

        foreach (var probe in new[] { TestVectors.OneHot(0), TestVectors.OneHot(1), TestVectors.Blend(0, 1, 0.5f) })
        {
            Assert.Equal(before.Probability(probe), after.Probability(probe));
        }
    }
}
