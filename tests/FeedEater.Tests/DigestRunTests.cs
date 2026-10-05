using Dapper;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DigestRunTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Today = "2026-10-05";
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 4, 30, 0, TimeSpan.Zero); // 07:30 Kyiv
    private static readonly string LongText = string.Join(' ', Enumerable.Repeat("Postgres async I/O details.", 80));

    private readonly List<string> _sent = [];
    private int _telegramCalls;
    private int _failTelegramAt;
    private int _chatCalls;
    private bool _budgetOnB;
    private bool _failAllTelegram;
    private bool _readThrows;
    private int _triageCalls;
    private bool _llmDown;
    private bool _readsDown;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Chat(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content } } },
        usage = new { prompt_tokens = 100, completion_tokens = 20 },
    });

    private HttpResponseMessage Llm(string body)
    {
        Interlocked.Increment(ref _chatCalls);
        var isTriage = body.Contains("\"model\":\"gpt-4.1-nano\"", StringComparison.Ordinal);
        if (_llmDown || (_readsDown && !isTriage))
        {
            return StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        }

        if (body.Contains("\"model\":\"gpt-4.1-nano\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _triageCalls);
            var keep = body.Contains("Title: Keep", StringComparison.Ordinal);
            return StubHandler.Json(Chat(keep ? """{"relevance":3,"project":"homelab","kind":"improve","reason":"r"}""" : """{"relevance":0,"kind":"fyi"}"""));
        }

        if (_readThrows)
        {
            throw new InvalidOperationException("read model exploded");
        }

        if (body.Contains("Title: Keep A", StringComparison.Ordinal))
        {
            return StubHandler.Json(Chat("""{"summary":"A is new.","why":"Box runs it.","kind":"improve","project":"homelab","suggestion":"Turn A on."}"""));
        }

        return _budgetOnB
            ? StubHandler.Json("""{"error":{"message":"ExceededTokenBudget"}}""", HttpStatusCode.Unauthorized)
            : StubHandler.Json(Chat("""{"summary":"B is out.","why":"Context.","kind":"fyi","project":null,"suggestion":null}"""));
    }

    private HttpResponseMessage Telegram(string body)
    {
        var n = Interlocked.Increment(ref _telegramCalls);
        if (n == _failTelegramAt || _failAllTelegram)
        {
            return StubHandler.Json("""{"ok":false,"description":"Too Many Requests"}""");
        }

        _sent.Add(body);
        return StubHandler.Json($$$"""{"ok":true,"result":{"message_id":{{{n}}}}}""");
    }

    private (DigestRun Run, DigestStore Digests, StubHandler Miniflux) Build()
    {
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json", Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var time = new FakeTimeProvider(Now);
        var miniflux = new StubHandler((_, _) => StubHandler.Json(JsonSerializer.Serialize(new { content = $"<p>{LongText}</p>" })));
        var llm = new StubHandler((_, body) => body.Contains("\"input\"", StringComparison.Ordinal)
            ? StubHandler.Json(TestVectors.EmbeddingResponse(1))
            : Llm(body));
        var telegram = new StubHandler((_, body) => Telegram(body));
        var digests = new DigestStore(pg.Db);
        var run = new DigestRun(
            new ItemStore(pg.Db), new ProfileStore(pg.Db), new FeedbackStore(pg.Db), new AnalysisStore(pg.Db), digests, new UsageStore(pg.Db),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new MinifluxClient(miniflux.Client("http://miniflux/")),
            new TelegramClient(telegram.Client("http://tg/botT/")),
            new LoopHealth(time), options, time, NullLogger<DigestRun>.Instance);
        return (run, digests, miniflux);
    }

    private async Task SeedAsync()
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [new Profile { Key = "homelab", Kind = "project", PlaneIdentifier = "LAB", Description = "Hetzner VPS.", Embedding = TestVectors.OneHot(0) }],
            Now, default);
        var hourAgo = Now.UtcDateTime.AddHours(-1);
        await Seed.ItemAsync(pg, 1, "Keep A", TestVectors.OneHot(0), hourAgo, hourAgo, LongText, 201);
        await Seed.ItemAsync(pg, 1, "Keep B", TestVectors.Blend(0, 1, 0.3f), hourAgo, hourAgo, "short", 202);
        await Seed.ItemAsync(pg, 2, "Drop C", TestVectors.Blend(0, 2, 0.6f), hourAgo, hourAgo, LongText, 203);
        // Backfilled today but published a month ago: archived, never a candidate.
        await Seed.ItemAsync(pg, 2, "Keep Old", TestVectors.OneHot(0), Now.UtcDateTime.AddDays(-30), hourAgo, LongText, 204);
    }

    [Fact]
    public async Task Sends_the_header_then_ideas_first_and_runs_only_once()
    {
        await SeedAsync();
        var (run, digests, miniflux) = Build();

        await run.RunAsync(Today, default);
        var chatCalls = _chatCalls;
        await run.RunAsync(Today, default);

        Assert.Equal(3, _sent.Count);
        Assert.Contains("2 of 3 new items", _sent[0], StringComparison.Ordinal);
        Assert.Contains("Keep A", _sent[1], StringComparison.Ordinal);
        Assert.Contains("Turn A on.", _sent[1], StringComparison.Ordinal);
        Assert.Contains("Keep B", _sent[2], StringComparison.Ordinal);
        Assert.DoesNotContain(_sent, s => s.Contains("Keep Old", StringComparison.Ordinal));
        Assert.Equal(chatCalls, _chatCalls);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Single(miniflux.Calls, c => c.Uri.Contains("/v1/entries/202/fetch-content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_header_carries_the_seven_day_up_rate()
    {
        await SeedAsync();
        var voted = await Seed.ItemAsync(pg, 3, "Voted", TestVectors.OneHot(5), Now.UtcDateTime.AddDays(-5), Now.UtcDateTime.AddDays(-5));
        await new FeedbackStore(pg.Db).SetVoteAsync(voted, 1, default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update votes set at = @at", new { at = Now.UtcDateTime.AddDays(-3) });
        }

        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Contains("7-day", _sent[0], StringComparison.Ordinal);
        Assert.Contains("100%", _sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_job_run_keeps_the_digest_retryable_and_reports_once()
    {
        await SeedAsync();
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json", Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var (run, digests, _) = Build();
        var time = new FakeTimeProvider(Now);
        var notices = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}"""));
        var job = new DigestJob(run, digests, new TelegramClient(notices.Client("http://tg/botT/")),
            new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<DigestJob>.Instance);
        _failAllTelegram = true;

        await Assert.ThrowsAsync<TelegramException>(() => job.TickAsync(default));
        await Assert.ThrowsAsync<TelegramException>(() => job.TickAsync(default));

        var row = (await digests.GetAsync(Today, default))!;
        Assert.NotEqual("failed", row.Status);
        Assert.NotNull(row.Error);
        Assert.Single(notices.Calls);
    }

    [Fact]
    public async Task Candidates_are_untriaged_embedded_items_inside_the_window()
    {
        var items = new ItemStore(pg.Db);
        var hourAgo = Now.UtcDateTime.AddHours(-1);
        var late = await Seed.ItemAsync(pg, 1, "Late embed", TestVectors.OneHot(0), hourAgo, hourAgo);
        var seen = await Seed.ItemAsync(pg, 1, "Seen", TestVectors.OneHot(0), hourAgo, hourAgo);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set embedding = null where id = @late", new { late });
        }

        await new AnalysisStore(pg.Db).SaveTriageAsync(seen, new TriageResult { Relevance = 0 }, "m", default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update triage set at = @at", new { at = Now.UtcDateTime.AddDays(-1) });
        }

        var floor = new DateTimeOffset(Now.UtcDateTime.AddDays(-3), TimeSpan.Zero);

        var dayStart = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(3));
        Assert.Empty(await items.CandidatesAsync(floor, dayStart, default));

        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set embedding = @v::real[]::vector where id = @late", new { late, v = TestVectors.OneHot(0) });
        }

        Assert.Equal([late], (await items.CandidatesAsync(floor, dayStart, default)).Select(x => x.Id));
    }

    [Fact]
    public async Task A_run_that_dies_after_triage_is_retried_the_same_day_without_new_triage_calls()
    {
        await SeedAsync();
        var (run, _, _) = Build();
        _readThrows = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(Today, default));
        var triageCalls = _triageCalls;
        Assert.True(triageCalls > 0);
        _readThrows = false;

        await run.RunAsync(Today, default);

        Assert.Equal(triageCalls, _triageCalls);
        Assert.Equal(3, _sent.Count);
        Assert.Contains("2 of 3 new items", _sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resumes_after_a_telegram_failure_without_paying_again()
    {
        await SeedAsync();
        var (run, digests, _) = Build();
        _failTelegramAt = 2;

        await Assert.ThrowsAsync<TelegramException>(() => run.RunAsync(Today, default));
        var afterFirst = _chatCalls;
        Assert.Equal(1, (await digests.GetAsync(Today, default))!.SentCount);

        await run.RunAsync(Today, default);

        Assert.Equal(afterFirst, _chatCalls);
        Assert.Equal(3, _sent.Count);
        Assert.Single(_sent, s => s.Contains("Feed digest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_spent_budget_still_sends_what_was_read_with_a_note()
    {
        await SeedAsync();
        _budgetOnB = true;
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Equal(2, _sent.Count);
        Assert.Contains("budget reached", _sent[0], StringComparison.Ordinal);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task No_candidates_sends_one_explanation_and_returns()
    {
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);
        await run.RunAsync(Today, default);

        Assert.Contains("No digest today", Assert.Single(_sent), StringComparison.Ordinal);
        Assert.Equal("failed", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task A_litellm_outage_during_triage_throws_so_the_job_retries_and_a_rerun_sends()
    {
        await SeedAsync();
        _llmDown = true;
        var (run, digests, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(Today, default));

        Assert.Empty(_sent);
        Assert.NotEqual("failed", (await digests.GetAsync(Today, default))!.Status);

        _llmDown = false;
        await run.RunAsync(Today, default);

        Assert.Equal(3, _sent.Count);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task A_litellm_outage_during_reads_throws_and_a_rerun_sends_without_new_triage()
    {
        await SeedAsync();
        _readsDown = true;
        var (run, digests, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(Today, default));

        Assert.Empty(_sent);
        Assert.NotEqual("failed", (await digests.GetAsync(Today, default))!.Status);

        _readsDown = false;
        var triageBefore = _triageCalls;
        await run.RunAsync(Today, default);

        Assert.Equal(triageBefore, _triageCalls);
        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public async Task A_new_days_run_closes_older_building_digests_as_failed()
    {
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, error, note) values ('2026-10-04', 'building', 'Telegram 429', null), ('2026-10-03', 'sent', null, null), ('2026-10-02', 'building', null, 'a note')");
        }

        var (run, digests, _) = Build();
        await run.RunAsync(Today, default);

        var stale = (await digests.GetAsync("2026-10-04", default))!;
        Assert.Equal("failed", stale.Status);
        Assert.Equal("Telegram 429", stale.Error);
        Assert.Contains("not sent before 12:00", stale.Note, StringComparison.Ordinal);
        var noted = (await digests.GetAsync("2026-10-02", default))!;
        Assert.Equal("failed", noted.Status);
        Assert.Equal("a note\nnot sent before 12:00", noted.Note);
        Assert.Equal("not sent before 12:00", noted.Error);
        Assert.Equal("sent", (await digests.GetAsync("2026-10-03", default))!.Status);
    }
}
