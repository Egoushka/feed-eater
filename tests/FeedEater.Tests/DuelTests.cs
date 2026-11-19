using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Duels;
using FeedEater.Inbox;
using FeedEater.Loops;
using FeedEater.Issues;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DuelTests(PostgresFixture pg) : IAsyncLifetime
{
    // 2026-10-06 is in EEST (UTC+3).
    private static DateTimeOffset Kyiv(int hour, int minute = 0) => new(2026, 10, 6, hour, minute, 0, TimeSpan.FromHours(3));

    private bool _inboxFails;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    /// <summary>An item the duel may offer: published a day before the first slot, scored, triaged at 2. Scores rise with <paramref name="n"/>.</summary>
    private async Task<long> PoolItemAsync(int n, string? title = null, long feed = 1, int relevance = 2, double? score = null, double daysOld = 1)
    {
        var id = await Seed.ItemAsync(pg, feed, title ?? $"Item {n}", TestVectors.OneHot(n), Kyiv(12, 30).UtcDateTime.AddDays(-daysOld));
        await SqlAsync("update items set score = @score where id = @id", new { id, score = score ?? n });
        await SqlAsync("insert into triage (item_id, relevance, kind, model) values (@id, @relevance, 'fyi', 'm')", new { id, relevance });
        return id;
    }

    private async Task<IReadOnlyList<long>> PoolAsync(int count)
    {
        var ids = new List<long>();
        for (var n = 0; n < count; n++)
        {
            ids.Add(await PoolItemAsync(n));
        }

        return ids;
    }

    private sealed class FlakyInbox(InboxService inner, Func<bool> fails) : IInbox
    {
        public Task<InboxPost> AddAsync(InboxEntry entry, CancellationToken ct) =>
            fails() ? throw new InvalidOperationException("inbox down") : inner.AddAsync(entry, ct);
    }

    private (DuelJob Job, FakeTimeProvider Time) BuildJob(DateTimeOffset now, int perDay = 2, TimeSpan? quietFrom = null, TimeSpan? quietTo = null)
    {
        var time = new FakeTimeProvider(now);
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Duel = new DuelOptions { PerDay = perDay },
            Quiet = new QuietOptions { From = quietFrom, To = quietTo },
        });
        var inbox = new FlakyInbox(new InboxService(new InboxStore(pg.Db), time), () => _inboxFails);
        var job = new DuelJob(
            new DuelStore(pg.Db), inbox, new QuietHours(new CursorStore(pg.Db), options, time),
            new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<DuelJob>.Instance);
        return (job, time);
    }

    private Task<int> SentAsync() => ScalarAsync<int>("select count(*)::int from inbox where kind = 'duel'");

    private static Task<DuelAnswer> AnswerAsync(DuelStore store, long duel, char pick) => store.AnswerAsync(duel, pick, default);

    private sealed record Vote(short Value, float Weight);

    private async Task<Vote?> VoteAsync(long itemId)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<Vote>("select value, weight from votes where item_id = @itemId", new { itemId });
    }

    [Theory]
    [InlineData(12, 29, null)]
    [InlineData(12, 30, "2026-10-06@12:30")]
    [InlineData(14, 30, "2026-10-06@12:30")]
    [InlineData(14, 31, null)]
    [InlineData(20, 30, "2026-10-06@20:30")]
    [InlineData(22, 31, null)]
    public void The_key_is_the_latest_slot_that_has_passed_while_it_is_within_two_hours(int hour, int minute, string? key)
    {
        var slots = DuelJob.Slots("12:30,20:30");

        Assert.Equal(key, DuelJob.LatestSlot(slots, Kyiv(hour, minute).DateTime));
    }

    [Fact]
    public void A_slot_after_midnight_still_counts_the_next_morning_and_bad_times_are_dropped()
    {
        Assert.Equal("2026-10-05@23:30", DuelJob.LatestSlot(DuelJob.Slots("23:30"), new DateTime(2026, 10, 6, 1, 0, 0)));
        Assert.Equal([new TimeSpan(9, 0, 0), new TimeSpan(12, 30, 0)], DuelJob.Slots(" 12:30, nonsense, 25:00, 09:00, 12:30 "));
        Assert.Empty(DuelJob.Slots(""));
    }

    [Fact]
    public async Task Each_slot_adds_one_duel_and_the_second_slot_a_new_pair()
    {
        await PoolAsync(11);
        var (job, time) = BuildJob(Kyiv(12, 30));

        await job.TickAsync(default);
        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(20, 30));
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(2, await SentAsync());
        var pairs = await new DuelStore(pg.Db).SeenPairsAsync(default);
        Assert.Equal(2, pairs.Count);
    }

    [Fact]
    public async Task A_duel_is_an_inbox_entry_with_both_items()
    {
        await PoolAsync(11);
        var (job, _) = BuildJob(Kyiv(13));

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(1, await SentAsync());
        var duel = await ScalarAsync<long>("select id from duels");
        Assert.Equal("duel", await ScalarAsync<string>("select kind from inbox where key = @k", new { k = $"duel:{duel}" }));
        Assert.Equal(duel, await ScalarAsync<long>("select ref_id from inbox where key = @k", new { k = $"duel:{duel}" }));
    }

    [Fact]
    public async Task A_delivered_duel_is_not_marked_failed()
    {
        await PoolAsync(11);
        var (job, _) = BuildJob(Kyiv(13));

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Null(await ScalarAsync<DateTime?>("select send_failed_at from duels"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from inbox where kind = 'duel'"));
    }

    [Theory]
    [InlineData(13, 45, 1)]
    [InlineData(14, 30, 1)]
    [InlineData(14, 31, 0)]
    public async Task A_slot_missed_while_down_is_sent_at_start_only_within_two_hours(int hour, int minute, int expected)
    {
        await PoolAsync(11);
        var (job, _) = BuildJob(Kyiv(hour, minute));

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(expected, await SentAsync());
    }

    [Fact]
    public async Task No_more_than_PerDay_are_sent_and_a_skipped_slot_is_not_caught_up()
    {
        await PoolAsync(11);
        var (job, time) = BuildJob(Kyiv(12, 30), perDay: 1);
        var cursors = new CursorStore(pg.Db);

        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(20, 30));
        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(21, 30));
        await job.TickAsync(default);

        Assert.Equal(1, await SentAsync());
        Assert.Equal("2026-10-06@20:30", await cursors.GetAsync("job:duel", default));
    }

    [Fact]
    public async Task The_cap_counts_the_local_day_not_the_last_24_hours()
    {
        var ids = await PoolAsync(11);
        await new DuelStore(pg.Db).CreateAsync(ids[0], ids[1], Kyiv(20, 30).AddDays(-1), default);
        var (job, _) = BuildJob(Kyiv(12, 30), perDay: 1);

        await job.TickAsync(default);

        Assert.Equal(1, await SentAsync());
    }

    [Fact]
    public async Task Quiet_hours_hold_a_duel_until_they_end_if_that_is_still_within_the_slots_two_hours()
    {
        await PoolAsync(11);
        var (job, time) = BuildJob(Kyiv(12, 30), quietFrom: new TimeSpan(12, 0, 0), quietTo: new TimeSpan(14, 0, 0));

        await job.TickAsync(default);
        Assert.Equal(0, await SentAsync());

        time.SetUtcNow(Kyiv(14, 5));
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(1, await SentAsync());
    }

    [Fact]
    public async Task A_pair_that_was_sent_is_not_offered_again_even_when_it_is_the_only_one_in_the_band()
    {
        await PoolAsync(6);
        var (job, time) = BuildJob(Kyiv(12, 30));

        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(20, 30));
        await job.TickAsync(default);

        Assert.Equal(1, await SentAsync());
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from duels where answered_at is null"));
    }

    [Fact]
    public async Task With_fewer_than_six_eligible_items_nothing_is_sent_and_the_slot_is_done()
    {
        await PoolAsync(5);
        var (job, _) = BuildJob(Kyiv(12, 30));

        await job.TickAsync(default);

        Assert.Equal(0, await SentAsync());
        Assert.Equal("2026-10-06@12:30", await new CursorStore(pg.Db).GetAsync("job:duel", default));
    }

    [Fact]
    public async Task A_failed_add_keeps_the_duel_marked_failed_and_the_retry_takes_another_pair()
    {
        await PoolAsync(11);
        var (job, _) = BuildJob(Kyiv(12, 30), perDay: 1);
        _inboxFails = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from duels where send_failed_at is not null"));
        Assert.Null(await new CursorStore(pg.Db).GetAsync("job:duel", default));

        _inboxFails = false;
        await job.TickAsync(default);   // the failed one does not count toward the day's cap

        Assert.Equal(1, await SentAsync());
        Assert.Equal(2, (await new DuelStore(pg.Db).SeenPairsAsync(default)).Count);
    }

    [Fact]
    public async Task A_pair_that_always_fails_to_be_added_is_not_picked_again_by_the_next_attempts()
    {
        await PoolAsync(11);
        var (job, _) = BuildJob(Kyiv(12, 30));
        _inboxFails = true;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));
        }

        Assert.Equal(3, (await new DuelStore(pg.Db).SeenPairsAsync(default)).Count);
        Assert.Equal(3, await ScalarAsync<int>("select count(*)::int from duels where send_failed_at is not null"));
    }

    [Fact]
    public async Task The_pool_leaves_out_voted_muted_unscored_untriaged_stale_duplicate_and_repeated_story_items()
    {
        var good = await PoolAsync(3);
        var voted = await PoolItemAsync(10);
        await new FeedbackStore(pg.Db).SetVoteAsync(voted, 1, default);
        await PoolItemAsync(11, feed: 2);
        await SqlAsync("update feeds set muted = true where id = 2");
        var unscored = await PoolItemAsync(12);
        await SqlAsync("update items set score = null where id = @unscored", new { unscored });
        await PoolItemAsync(13, relevance: 0);
        await PoolItemAsync(14, daysOld: 8);
        var duplicate = await PoolItemAsync(15);
        await SqlAsync("update items set duplicate_of = @of where id = @duplicate", new { of = good[0], duplicate });
        var member = await PoolItemAsync(16);
        await SqlAsync("update items set cluster_of = @of where id = @member", new { of = good[1], member });
        await Seed.ItemAsync(pg, 1, "No triage", TestVectors.OneHot(17), Kyiv(12, 30).UtcDateTime.AddDays(-1));

        var pool = await new DuelStore(pg.Db).PoolAsync(Kyiv(12, 30).AddDays(-7), default);

        Assert.Equal(good, pool.Select(i => i.Id).Order().ToList());
        Assert.All(pool, i => Assert.Equal("Feed 1", i.Feed));
    }

    [Theory]
    [InlineData('a', 0, 1)]
    [InlineData('b', 1, 0)]
    public async Task The_winner_gets_a_thumbs_up_and_the_loser_a_thumbs_down_at_half_weight(char pick, int winner, int loser)
    {
        var ids = new[] { await PoolItemAsync(0, "Alpha <b>"), await PoolItemAsync(1, "Beta") };
        var store = new DuelStore(pg.Db);
        var duel = await store.CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        var answer = await AnswerAsync(store, duel, pick);

        Assert.Equal(DuelOutcome.Answered, answer.Outcome);
        Assert.Equal(winner == 0 ? "Alpha <b>" : "Beta", answer.Title);
        Assert.Equal(new Vote(1, 1f), await VoteAsync(ids[winner]));
        Assert.Equal(new Vote(-1, 0.5f), await VoteAsync(ids[loser]));
        Assert.Equal(ids[winner], await ScalarAsync<long>("select winner from duels where id = @duel", new { duel }));
    }

    [Fact]
    public async Task An_item_that_already_has_a_vote_keeps_it()
    {
        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var feedback = new FeedbackStore(pg.Db);
        await feedback.SetVoteAsync(ids[0], -1, default, weight: 2);
        await feedback.SetVoteAsync(ids[1], 1, default, weight: 3);
        var store = new DuelStore(pg.Db);
        var duel = await store.CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        await AnswerAsync(store, duel, 'a');

        Assert.Equal(new Vote(-1, 2f), await VoteAsync(ids[0]));
        Assert.Equal(new Vote(1, 3f), await VoteAsync(ids[1]));
    }

    [Fact]
    public async Task Skip_writes_no_votes_closes_the_duel_and_the_pair_is_not_offered_again()
    {
        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var store = new DuelStore(pg.Db);
        var duel = await store.CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        var answer = await AnswerAsync(store, duel, 's');

        Assert.Null(answer.Title);
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from votes"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from duels where answered_at is not null and winner is null"));
        Assert.Contains((ids[0], ids[1]), await store.SeenPairsAsync(default));
    }

    [Fact]
    public async Task A_second_answer_is_ignored_and_does_not_overwrite_a_vote_cast_since()
    {
        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var store = new DuelStore(pg.Db);
        var duel = await store.CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        await AnswerAsync(store, duel, 'a');
        await new FeedbackStore(pg.Db).SetVoteAsync(ids[1], 1, default, weight: 3);
        var second = await AnswerAsync(store, duel, 'b');

        Assert.Equal(DuelOutcome.AlreadyAnswered, second.Outcome);
        Assert.Equal(new Vote(1, 1f), await VoteAsync(ids[0]));
        Assert.Equal(new Vote(1, 3f), await VoteAsync(ids[1]));
    }

    [Fact]
    public async Task An_answer_to_a_duel_that_does_not_exist_says_so()
    {
        var answer = await AnswerAsync(new DuelStore(pg.Db), 99, 'a');

        Assert.Equal(DuelOutcome.NoDuel, answer.Outcome);
    }
}
