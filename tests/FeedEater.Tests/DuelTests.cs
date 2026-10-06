using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Duels;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DuelTests(PostgresFixture pg) : IAsyncLifetime
{
    // 2026-10-06 is in EEST (UTC+3).
    private static DateTimeOffset Kyiv(int hour, int minute = 0) => new(2026, 10, 6, hour, minute, 0, TimeSpan.FromHours(3));

    private readonly List<(string Method, string Body)> _telegram = [];
    private bool _telegramFails;
    private bool _editFails;

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

    private (DuelJob Job, FakeTimeProvider Time, List<string> Sent) BuildJob(DateTimeOffset now, int perDay = 2, TimeSpan? quietFrom = null, TimeSpan? quietTo = null)
    {
        var time = new FakeTimeProvider(now);
        var sent = new List<string>();
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Telegram = new TelegramOptions { AllowedUserId = 42 },
            Duel = new DuelOptions { PerDay = perDay },
            Quiet = new QuietOptions { From = quietFrom, To = quietTo },
        });
        var stub = new StubHandler((_, body) =>
        {
            if (_telegramFails)
            {
                return StubHandler.Json("""{"ok":false,"description":"Too Many Requests"}""");
            }

            sent.Add(body);
            return StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""");
        });
        var job = new DuelJob(
            new DuelStore(pg.Db), new TelegramClient(stub.Client("http://tg/botT/")), new QuietHours(new CursorStore(pg.Db), options, time),
            new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<DuelJob>.Instance);
        return (job, time, sent);
    }

    private CallbackHandler BuildHandler()
    {
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var telegram = new TelegramClient(new StubHandler((request, body) =>
        {
            var method = request.RequestUri!.Segments[^1];
            _telegram.Add((method, body));
            return method == "editMessageText" && _editFails
                ? StubHandler.Json("""{"ok":false,"description":"Bad Request: message can't be edited"}""")
                : StubHandler.Json("""{"ok":true,"result":true}""");
        }).Client("http://tg/botT/"));
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        IIdeaSink sink = new PlaneIdeaSink(new PlaneClient(new StubHandler((_, _) => StubHandler.Json("{}")).Client("http://plane/"), options), options);
        var filer = new IdeaFiler(items, feedback, new ProfileStore(pg.Db), sink, TimeProvider.System);
        return new CallbackHandler(telegram, feedback, filer, items, new DuelStore(pg.Db), new FeedEater.Signals.KarakeepClient(new StubHandler((_, _) => StubHandler.Json("{}")).Client("http://k/")), options, NullLogger<CallbackHandler>.Instance);
    }

    private static TgCallback Tap(long duelId, char pick, long from = 42) => new("cb1", from, 42, 7, CallbackData.Duel(duelId, pick));

    private string Answers() => string.Join('\n', _telegram.Where(t => t.Method == "answerCallbackQuery").Select(t => t.Body));

    private sealed record Vote(short Value, float Weight);

    private async Task<Vote?> VoteAsync(long itemId)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<Vote>("select value, weight from votes where item_id = @itemId", new { itemId });
    }

    private static string Text(string body) => JsonSerializer.Deserialize<JsonElement>(body).GetProperty("text").GetString()!;

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
    public async Task Each_slot_sends_one_duel_with_three_buttons_and_the_second_slot_a_new_pair()
    {
        await PoolAsync(11);
        var (job, time, sent) = BuildJob(Kyiv(12, 30));

        await job.TickAsync(default);
        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(20, 30));
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(2, sent.Count);
        Assert.Contains("first", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"callback_data\":\"d:1:a\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"callback_data\":\"d:1:b\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"callback_data\":\"d:1:s\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"callback_data\":\"d:2:a\"", sent[1], StringComparison.Ordinal);
        var pairs = await new DuelStore(pg.Db).SeenPairsAsync(default);
        Assert.Equal(2, pairs.Count);
    }

    [Theory]
    [InlineData(13, 45, 1)]
    [InlineData(14, 30, 1)]
    [InlineData(14, 31, 0)]
    public async Task A_slot_missed_while_down_is_sent_at_start_only_within_two_hours(int hour, int minute, int expected)
    {
        await PoolAsync(11);
        var (job, _, sent) = BuildJob(Kyiv(hour, minute));

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(expected, sent.Count);
    }

    [Fact]
    public async Task No_more_than_PerDay_are_sent_and_a_skipped_slot_is_not_caught_up()
    {
        await PoolAsync(11);
        var (job, time, sent) = BuildJob(Kyiv(12, 30), perDay: 1);
        var cursors = new CursorStore(pg.Db);

        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(20, 30));
        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(21, 30));
        await job.TickAsync(default);

        Assert.Single(sent);
        Assert.Equal("2026-10-06@20:30", await cursors.GetAsync("job:duel", default));
    }

    [Fact]
    public async Task The_cap_counts_the_local_day_not_the_last_24_hours()
    {
        var ids = await PoolAsync(11);
        await new DuelStore(pg.Db).CreateAsync(ids[0], ids[1], Kyiv(20, 30).AddDays(-1), default);
        var (job, _, sent) = BuildJob(Kyiv(12, 30), perDay: 1);

        await job.TickAsync(default);

        Assert.Single(sent);
    }

    [Fact]
    public async Task Quiet_hours_hold_a_duel_until_they_end_if_that_is_still_within_the_slots_two_hours()
    {
        await PoolAsync(11);
        var (job, time, sent) = BuildJob(Kyiv(12, 30), quietFrom: new TimeSpan(12, 0, 0), quietTo: new TimeSpan(14, 0, 0));

        await job.TickAsync(default);
        Assert.Empty(sent);

        time.SetUtcNow(Kyiv(14, 5));
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Single(sent);
    }

    [Fact]
    public async Task A_pair_that_was_sent_is_not_offered_again_even_when_it_is_the_only_one_in_the_band()
    {
        await PoolAsync(6);
        var (job, time, sent) = BuildJob(Kyiv(12, 30));

        await job.TickAsync(default);
        time.SetUtcNow(Kyiv(20, 30));
        await job.TickAsync(default);

        Assert.Single(sent);
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from duels where answered_at is null"));
    }

    [Fact]
    public async Task With_fewer_than_six_eligible_items_nothing_is_sent_and_the_slot_is_done()
    {
        await PoolAsync(5);
        var (job, _, sent) = BuildJob(Kyiv(12, 30));

        await job.TickAsync(default);

        Assert.Empty(sent);
        Assert.Equal("2026-10-06@12:30", await new CursorStore(pg.Db).GetAsync("job:duel", default));
    }

    [Fact]
    public async Task A_failed_send_leaves_no_duel_behind_and_is_retried()
    {
        await PoolAsync(11);
        var (job, _, sent) = BuildJob(Kyiv(12, 30));
        _telegramFails = true;

        await Assert.ThrowsAsync<TelegramException>(() => job.TickAsync(default));
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from duels"));
        Assert.Null(await new CursorStore(pg.Db).GetAsync("job:duel", default));

        _telegramFails = false;
        await job.TickAsync(default);

        Assert.Single(sent);
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from duels"));
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

    [Fact]
    public void Titles_feeds_and_links_are_escaped_and_clipped_in_the_duel_and_after_the_tap()
    {
        var hostile = new DuelItem { Id = 1, StoryId = 1, Title = "<script>alert(1)</script> & \"q\"", Feed = "<b>feed</b>", Url = "https://e.example/?a=\"><i>" };
        var plain = new DuelItem { Id = 2, StoryId = 2, Title = new string('x', 500), Feed = "", Url = "javascript:alert(1)" };

        var message = DuelFormatter.Message(9, hostile, plain);

        Assert.DoesNotContain("<script>", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>feed</b>", message.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;q&quot;", message.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://e.example/?a=&quot;&gt;&lt;i&gt;\"", message.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;feed&lt;/b&gt;", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 400), message.Html, StringComparison.Ordinal);
        Assert.Equal(["d:9:a", "d:9:b", "d:9:s"], message.Keyboard!.Single().Select(b => b.Data));
        Assert.True(message.Keyboard!.Single().All(b => System.Text.Encoding.UTF8.GetByteCount(b.Data) <= 64));
        Assert.Equal("You picked &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;q&quot;", DuelFormatter.Picked(hostile.Title));
        Assert.Equal("Skipped", DuelFormatter.Picked(null));
    }

    [Theory]
    [InlineData('a', 0, 1)]
    [InlineData('b', 1, 0)]
    public async Task The_winner_gets_a_thumbs_up_and_the_loser_a_thumbs_down_at_half_weight(char pick, int winner, int loser)
    {
        var ids = new[] { await PoolItemAsync(0, "Alpha <b>"), await PoolItemAsync(1, "Beta") };
        var duel = await new DuelStore(pg.Db).CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        await BuildHandler().HandleAsync(Tap(duel, pick), default);

        Assert.Equal(new Vote(1, 1f), await VoteAsync(ids[winner]));
        Assert.Equal(new Vote(-1, 0.5f), await VoteAsync(ids[loser]));
        Assert.Equal(ids[winner], await ScalarAsync<long>("select winner from duels where id = @duel", new { duel }));
        var edit = _telegram.Single(t => t.Method == "editMessageText").Body;
        Assert.Equal("You picked " + (winner == 0 ? "Alpha &lt;b&gt;" : "Beta"), Text(edit));
        Assert.DoesNotContain("reply_markup", edit, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":7", edit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_that_already_has_a_vote_keeps_it()
    {
        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var feedback = new FeedbackStore(pg.Db);
        await feedback.SetVoteAsync(ids[0], -1, default, weight: 2);
        await feedback.SetVoteAsync(ids[1], 1, default, weight: 3);
        var duel = await new DuelStore(pg.Db).CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        await BuildHandler().HandleAsync(Tap(duel, 'a'), default);

        Assert.Equal(new Vote(-1, 2f), await VoteAsync(ids[0]));
        Assert.Equal(new Vote(1, 3f), await VoteAsync(ids[1]));
        Assert.Contains("You picked", _telegram.Single(t => t.Method == "editMessageText").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skip_writes_no_votes_closes_the_duel_and_the_pair_is_not_offered_again()
    {
        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var store = new DuelStore(pg.Db);
        var duel = await store.CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        await BuildHandler().HandleAsync(Tap(duel, 's'), default);

        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from votes"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from duels where answered_at is not null and winner is null"));
        Assert.Equal("Skipped", Text(_telegram.Single(t => t.Method == "editMessageText").Body));
        Assert.Contains((ids[0], ids[1]), await store.SeenPairsAsync(default));
    }

    [Fact]
    public async Task Someone_elses_tap_changes_nothing()
    {
        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var duel = await new DuelStore(pg.Db).CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);

        await BuildHandler().HandleAsync(Tap(duel, 'a', from: 7), default);

        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from votes"));
        Assert.Null(await ScalarAsync<DateTime?>("select answered_at from duels where id = @duel", new { duel }));
        Assert.DoesNotContain(_telegram, t => t.Method == "editMessageText");
        Assert.Contains("Not for you.", Answers(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_tap_is_ignored_and_does_not_overwrite_a_vote_cast_since()
    {
        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var duel = await new DuelStore(pg.Db).CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);
        var handler = BuildHandler();

        await handler.HandleAsync(Tap(duel, 'a'), default);
        await new FeedbackStore(pg.Db).SetVoteAsync(ids[1], 1, default, weight: 3);
        await handler.HandleAsync(Tap(duel, 'b'), default);

        Assert.Equal(new Vote(1, 1f), await VoteAsync(ids[0]));
        Assert.Equal(new Vote(1, 3f), await VoteAsync(ids[1]));
        Assert.Equal(1, _telegram.Count(t => t.Method == "editMessageText"));
        Assert.Contains("Already answered", Answers(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tap_on_a_duel_that_does_not_exist_is_answered_and_a_failed_edit_does_not_lose_the_votes()
    {
        var handler = BuildHandler();
        await handler.HandleAsync(Tap(99, 'a'), default);
        Assert.Contains("No such duel", Answers(), StringComparison.Ordinal);

        var ids = new[] { await PoolItemAsync(0), await PoolItemAsync(1) };
        var duel = await new DuelStore(pg.Db).CreateAsync(ids[0], ids[1], DateTimeOffset.UtcNow, default);
        _editFails = true;

        await handler.HandleAsync(Tap(duel, 'a'), default);

        Assert.Equal(new Vote(1, 1f), await VoteAsync(ids[0]));
        Assert.Contains("saved", Answers(), StringComparison.Ordinal);
    }
}
