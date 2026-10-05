using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Review;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class WeeklyTests(PostgresFixture pg) : IAsyncLifetime
{
    // Sunday 2026-10-11 18:30 Kyiv
    private static readonly DateTimeOffset Until = new(2026, 10, 11, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Since = Until.AddDays(-7);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<long> ItemAsync(long feed, string title, double daysAgo) =>
        Seed.ItemAsync(pg, feed, title, TestVectors.OneHot((int)(Math.Abs(title.GetHashCode()) % 1000)), Until.UtcDateTime.AddDays(-daysAgo));

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(sql, args);
    }

    private Task<WeeklyReport> BuildAsync() => new WeeklyStore(pg.Db).BuildAsync(Since, Until, "2026-10-04", "2026-10-11", default);

    private async Task SeedWeekAsync()
    {
        var feedback = new FeedbackStore(pg.Db);
        var liked1 = await ItemAsync(1, "Liked one", 1);
        var liked2 = await ItemAsync(1, "Liked two", 2);
        var disliked = await ItemAsync(2, "Disliked", 3);
        var shownOnly = await ItemAsync(2, "Shown but unrated", 2);
        await ItemAsync(3, "Noisy a", 1);
        await ItemAsync(3, "Noisy b", 2);
        await ItemAsync(3, "Noisy c", 3);
        await ItemAsync(4, "Muted noise", 1);
        await ItemAsync(1, "Last week", 9);
        await SqlAsync("update feeds set muted = true where id = 4");
        await Seed.ReadAsync(pg, liked1, "homelab", "improve");
        await Seed.ReadAsync(pg, liked2, "postgres", "fyi");
        await feedback.SetVoteAsync(liked1, 1, default);
        await feedback.SetVoteAsync(liked2, 1, default);
        await feedback.SetVoteAsync(disliked, -1, default);
        await feedback.AddIdeaAsync(new Idea { ItemId = liked1, PlaneProject = "LAB", PlaneIssueId = "x", Title = "Try it", At = Until.UtcDateTime.AddDays(-1) }, default);
        await SqlAsync("update votes set at = @at", new { at = Until.UtcDateTime.AddDays(-1) });
        await SqlAsync("insert into digests (local_date, status, item_ids) values ('2026-10-08', 'sent', @ids), ('2026-10-01', 'sent', @old)",
            new { ids = new[] { liked1, shownOnly }, old = new[] { liked2 } });
    }

    [Fact]
    public async Task The_report_counts_the_week_and_ranks_what_was_rated()
    {
        await SeedWeekAsync();

        var r = await BuildAsync();

        Assert.Equal((8, 2, 2, 1, 1), (r.Items, r.Shown, r.Up, r.Down, r.Ideas));   // "Last week" and the 1 Oct digest are outside
        Assert.Equal(["Liked one", "Liked two"], r.TopLiked.Select(i => i.Title).Order());
        Assert.Equal([("homelab", 1, 0), ("other", 0, 1), ("postgres", 1, 0)], r.Projects.OrderBy(p => p.Project).Select(p => (p.Project, p.Up, p.Down)));
        Assert.Equal(("Try it", "LAB"), (r.IdeasFiled.Single().Title, r.IdeasFiled.Single().PlaneProject));
        Assert.Equal(("Feed 1", 2), (r.TopFeeds.Single().Title, r.TopFeeds.Single().Count));
        Assert.Equal([("Feed 3", 3)], r.MuteCandidates.Select(f => (f.Title, f.Count)));   // feed 2 had one shown, feed 4 is muted already
    }

    [Fact]
    public async Task An_empty_week_gives_zeros_and_empty_lists()
    {
        var r = await BuildAsync();

        Assert.Equal((0, 0, 0, 0, 0), (r.Items, r.Shown, r.Up, r.Down, r.Ideas));
        Assert.Empty(r.TopLiked);
        Assert.Empty(r.Projects);
        Assert.Empty(r.MuteCandidates);
    }

    [Fact]
    public async Task Top_liked_is_capped_at_five_and_feeds_at_three()
    {
        var feedback = new FeedbackStore(pg.Db);
        for (var i = 0; i < 8; i++)
        {
            await feedback.SetVoteAsync(await ItemAsync(1 + i % 5, $"Liked {i}", 1), 1, default);
        }

        await SqlAsync("update votes set at = @at", new { at = Until.UtcDateTime.AddDays(-1) });
        var r = await BuildAsync();

        Assert.Equal(5, r.TopLiked.Count);
        Assert.Equal(3, r.TopFeeds.Count);
        Assert.Equal(2, r.TopFeeds[0].Count);
    }

    [Fact]
    public async Task A_review_round_trips_and_saving_again_replaces_it_and_clears_sent()
    {
        await SeedWeekAsync();
        var store = new WeeklyStore(pg.Db);
        var report = await BuildAsync();

        await store.SaveAsync("2026-10-11", Until, report, "<b>hi</b>", default);
        await store.MarkSentAsync("2026-10-11", Until.AddMinutes(1), default);
        var row = (await store.GetAsync("2026-10-11", default))!;

        Assert.Equal(report.Up, row.Report.Up);
        Assert.Equal(report.TopLiked.Select(i => i.Title), row.Report.TopLiked.Select(i => i.Title));
        Assert.Equal(Until.AddMinutes(1), row.SentAt);
        Assert.Equal("<b>hi</b>", row.Message);

        await store.SaveAsync("2026-10-11", Until, report, "<b>again</b>", default);
        await store.SaveAsync("2026-10-04", Until.AddDays(-7), report, "old", default);

        Assert.Null((await store.GetAsync("2026-10-11", default))!.SentAt);
        Assert.Equal("2026-10-11", (await store.LatestAsync(default))!.WeekOf);
        Assert.Equal(["2026-10-11", "2026-10-04"], await store.ListAsync(10, default));
        Assert.Null(await store.GetAsync("2026-01-01", default));
    }

    [Fact]
    public async Task The_message_is_encoded_capped_and_links_only_safe_urls()
    {
        var items = Enumerable.Range(0, 5).Select(i => new WeeklyItem(i, $"<script>{i}</script>", i == 0 ? "javascript:alert(1)" : "https://e.example/?a=1&b=2", "<Feed>")).ToList();
        var report = new WeeklyReport(Since, Until, 10, 4, 5, 1, 12, items,
            Enumerable.Range(0, 12).Select(i => new ProjectTally($"p{i}", 1, 0)).ToList(),
            Enumerable.Range(0, 12).Select(i => new WeeklyIdea(i, $"Idea <{i}>", "LAB")).ToList(),
            [new FeedCount(1, "A & B", 3)], [new FeedCount(2, "Noisy", 40)]);

        var html = WeeklyFormatter.Message("2026-10-11", report, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).Html;

        Assert.StartsWith("<b>Weekly review · 4 Oct to 11 Oct</b>", html, StringComparison.Ordinal);
        Assert.Contains("10 items · 4 shown in digests · 👍 5 · 👎 1 · 💡 12", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://e.example/?a=1&amp;b=2\">", html, StringComparison.Ordinal);
        Assert.Contains("+4 more", html, StringComparison.Ordinal);
        Assert.DoesNotContain("p8 ", html, StringComparison.Ordinal);
        Assert.Contains("A &amp; B 3", html, StringComparison.Ordinal);
        Assert.Contains("Noisy 40", html, StringComparison.Ordinal);
        Assert.True(html.Length < 4096);
    }

    private (WeeklyReview Job, FakeTimeProvider Time, List<string> Sent) BuildJob(DateTimeOffset now, bool telegramFails = false)
    {
        var time = new FakeTimeProvider(now);
        var sent = new List<string>();
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var stub = new StubHandler((_, body) =>
        {
            if (telegramFails)
            {
                return StubHandler.Json("""{"ok":false,"description":"Too Many Requests"}""");
            }

            sent.Add(body);
            return StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""");
        });
        return (new WeeklyReview(new WeeklyStore(pg.Db), new TelegramClient(stub.Client("http://tg/botT/")), new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<WeeklyReview>.Instance), time, sent);
    }

    [Fact]
    public async Task The_job_runs_once_on_sunday_evening_stores_the_review_and_sends_it()
    {
        await SeedWeekAsync();
        var (job, time, sent) = BuildJob(Until.AddMinutes(-60));   // 17:30 Kyiv: not due yet, last Sunday's key is the latest

        await job.TickAsync(default);
        var firstRunSent = sent.Count;

        time.SetUtcNow(Until.AddMinutes(1));
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(1, firstRunSent);   // a missed Sunday runs at start; the key is last Sunday's, so the figures are of the window ending now
        Assert.Equal(2, sent.Count);
        Assert.Contains("Weekly review", sent[^1], StringComparison.Ordinal);
        var row = (await new WeeklyStore(pg.Db).LatestAsync(default))!;
        Assert.Equal("2026-10-11", row.WeekOf);
        Assert.NotNull(row.SentAt);
    }

    [Fact]
    public async Task A_quiet_week_sends_nothing_and_a_failed_send_is_retried()
    {
        var (quiet, _, sent) = BuildJob(Until.AddMinutes(1));
        await quiet.TickAsync(default);
        Assert.Empty(sent);
        Assert.Null(await new WeeklyStore(pg.Db).LatestAsync(default));

        await pg.ResetAsync();   // forget the quiet run's done-marker
        await SeedWeekAsync();
        var (failing, _, _) = BuildJob(Until.AddMinutes(1), telegramFails: true);
        await Assert.ThrowsAsync<TelegramException>(() => failing.TickAsync(default));
        await Assert.ThrowsAsync<TelegramException>(() => failing.TickAsync(default));

        var row = (await new WeeklyStore(pg.Db).LatestAsync(default))!;
        Assert.Null(row.SentAt);   // stored, not marked sent, so the UI has it and the job retries
    }
}
