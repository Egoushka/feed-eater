using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Issues;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.WeeklyIdeas;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DailyIdeasJobTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Evening = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);   // 21:00 Kyiv, after the 20:00 run time

    private string _chatReply = "";
    private readonly List<string> _chatBodies = [];
    private readonly List<string> _logs = [];

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (DailyIdeasJob Job, StubHandler Telegram) Build(TimeProvider time)
    {
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Telegram = new TelegramOptions { AllowedUserId = 42, Token = "T" },
            GitHub = new GitHubOptions { Token = "t", FallbackRepo = "octo/feed" },
            DailyIdeas = new DailyIdeasOptions { Enabled = true },
        });
        var github = new StubHandler((_, _) => StubHandler.Json("[]"));
        var llm = new StubHandler((_, body) =>
        {
            _chatBodies.Add(body);
            return StubHandler.Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = _chatReply } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } }));
        });
        var telegram = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}"""));
        var backlog = new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.BadGateway));
        var cursors = new CursorStore(pg.Db);
        var job = new DailyIdeasJob(
            new WeeklyIdeaStore(pg.Db), new ProfileStore(pg.Db), new GitHubIssues(github.Client("http://github/")),
            new BacklogClient(backlog.Client("http://backlog/"), options),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new TelegramClient(telegram.Client("http://tg/botT/")), new InboxStore(pg.Db), new QuietHours(cursors, options, time),
            cursors, options, new LoopHealth(time), time, new ListLogger<DailyIdeasJob>(_logs));
        return (job, telegram);
    }

    /// <summary>Items liked at <paramref name="at"/>, one per title.</summary>
    private async Task LikedAsync(DateTimeOffset at, params string[] titles)
    {
        var feedback = new FeedbackStore(pg.Db);
        var ids = new List<long>();
        foreach (var title in titles)
        {
            var id = await Seed.ItemAsync(pg, 1, title, TestVectors.OneHot(ids.Count + 1));
            await feedback.SetVoteAsync(id, 1, default);
            ids.Add(id);
        }

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update votes set at = @at where item_id = any(@ids)", new { at = at.UtcDateTime, ids = ids.ToArray() });
    }

    private async Task<int> StagedAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>("select count(*) from inbox where kind = 'idea'");
    }

    private static string Ideas(int count) =>
        JsonSerializer.Serialize(new { ideas = Enumerable.Range(1, count).Select(i => new { lead = i, idea = $"Idea {i}", project = "octo/feed" }) });

    [Fact]
    public async Task A_day_with_nothing_new_asks_nothing_sends_nothing_and_is_done()
    {
        await LikedAsync(Evening.AddDays(-2), "Liked two days ago");   // outside the last 24 hours
        _chatReply = Ideas(1);
        var (job, telegram) = Build(new FakeTimeProvider(Evening));

        await job.TickAsync(default);

        Assert.Empty(_chatBodies);
        Assert.Empty(telegram.Calls);
        Assert.Equal(0, await StagedAsync());
        Assert.Contains(_logs, l => l.Contains("No leads this day", StringComparison.Ordinal));
        Assert.Equal("2026-10-19", await new CursorStore(pg.Db).GetAsync("job:daily-ideas", default));   // skipped, not retried
    }

    [Fact]
    public async Task A_day_stages_at_most_five_ideas_from_the_last_24_hours()
    {
        await LikedAsync(Evening.AddHours(-3), "New 1", "New 2", "New 3", "New 4", "New 5", "New 6");
        await LikedAsync(Evening.AddHours(-30), "Yesterday item");
        _chatReply = Ideas(6);
        var (job, telegram) = Build(new FakeTimeProvider(Evening));

        await job.TickAsync(default);

        var prompt = Assert.Single(_chatBodies);
        Assert.Contains("one day of the owner", prompt, StringComparison.Ordinal);
        Assert.Contains("at most 5 leads", prompt, StringComparison.Ordinal);
        Assert.Contains("New 6", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Yesterday item", prompt, StringComparison.Ordinal);
        Assert.Equal(5, await StagedAsync());
        var message = Assert.Single(telegram.Calls);
        Assert.Contains("Ideas for 2026-10-19", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_once_a_day_and_a_source_staged_before_is_not_offered_again()
    {
        await LikedAsync(Evening.AddHours(-3), "Staged today");
        _chatReply = Ideas(1);
        var time = new FakeTimeProvider(Evening);
        var (job, telegram) = Build(time);

        await job.TickAsync(default);
        await job.TickAsync(default);
        Assert.Single(_chatBodies);

        time.Advance(TimeSpan.FromDays(1));
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update votes set at = @at", new { at = time.GetUtcNow().AddHours(-1).UtcDateTime });   // liked again, still the same source
        }

        await job.TickAsync(default);

        Assert.Single(_chatBodies);   // the next day had nothing new
        Assert.Single(telegram.Calls);
        Assert.Equal(1, await StagedAsync());
        Assert.Equal("2026-10-20", await new CursorStore(pg.Db).GetAsync("job:daily-ideas", default));
    }
}
