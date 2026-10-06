using Dapper;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Signals;
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
    private readonly LoopHealth _health = new(new FakeTimeProvider(Now));
    private int _telegramCalls;
    private int _failTelegramAt;
    private int _chatCalls;
    private bool _budgetOnB;
    private bool _failAllTelegram;
    private bool _readThrows;
    private int _triageCalls;
    private bool _llmDown;
    private bool _readsDown;
    private bool _learn;
    private System.Net.HttpStatusCode _githubStatus = HttpStatusCode.NotFound;
    private readonly List<string> _readPrompts = [];
    private readonly List<string> _githubCalls = [];

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

        _readPrompts.Add(body);

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
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json", Telegram = new TelegramOptions { AllowedUserId = 42 }, Taste = new TasteOptions { Learn = _learn } });
        var time = new FakeTimeProvider(Now);
        var miniflux = new StubHandler((_, _) => StubHandler.Json(JsonSerializer.Serialize(new { content = $"<p>{LongText}</p>" })));
        var llm = new StubHandler((_, body) => body.Contains("\"input\"", StringComparison.Ordinal)
            ? StubHandler.Json(TestVectors.EmbeddingResponse(1))
            : Llm(body));
        var telegram = new StubHandler((_, body) => Telegram(body));
        var digests = new DigestStore(pg.Db);
        var run = new DigestRun(
            new ItemStore(pg.Db), new ReleaseStore(pg.Db), new ProfileStore(pg.Db), new FeedbackStore(pg.Db), new AnalysisStore(pg.Db), digests, new UsageStore(pg.Db),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new MinifluxClient(miniflux.Client("http://miniflux/")),
            new GitHubStarsClient(new StubHandler((request, _) => GitHub(request)).Client("http://github/"), options),
            new TelegramClient(telegram.Client("http://tg/botT/")),
            _health, options, time, NullLogger<DigestRun>.Instance);
        return (run, digests, miniflux);
    }

    private HttpResponseMessage GitHub(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        _githubCalls.Add(path);
        if (_githubStatus != HttpStatusCode.OK)
        {
            return StubHandler.Json("""{"message":"nope"}""", _githubStatus);
        }

        return path.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? StubHandler.Json("""{"tag_name":"v2.3.0","published_at":"2026-09-20T10:00:00Z"}""")
            : StubHandler.Json("""{"full_name":"acme/widget","created_at":"2024-03-02T08:00:00Z","pushed_at":"2026-10-01T09:00:00Z","stargazers_count":1240}""");
    }

    private async Task LinkRepoAsync(params string[] titles)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update items set content = @content where title = any(@titles)", new { content = LongText + " Source: https://github.com/acme/widget/issues/4", titles });
    }

    private DigestJob BuildJob(DigestRun run, DigestStore digests, StubHandler notices, IOptions<FeedEaterOptions> options, FakeTimeProvider time) =>
        new(run, digests, new TelegramClient(notices.Client("http://tg/botT/")), new DigestTrigger(new CursorStore(pg.Db), options, time),
            new QuietHours(new CursorStore(pg.Db), options, time), new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<DigestJob>.Instance);

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
    public async Task An_embedding_outage_is_not_reported_as_a_Miniflux_outage()
    {
        await SeedAsync();
        _health.Failed(Ingestor.EmbedName, "LiteLLM down");
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);
        Assert.Contains("Embeddings were unavailable", _sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Miniflux was unreachable", _sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Miniflux_outage_is_reported_as_one()
    {
        await SeedAsync();
        _health.Failed(Ingestor.LoopName, "refused");
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);
        Assert.Contains("Miniflux was unreachable", _sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Embeddings were unavailable", _sent[0], StringComparison.Ordinal);
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
        var job = BuildJob(run, digests, notices, options, time);
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

    private static readonly DateTimeOffset Evening = Now.AddHours(10);   // 17:30 Kyiv: outside the window

    private (DigestRun Run, DigestStore Digests, DigestJob Job, DigestTrigger Trigger, StubHandler Notices) BuildForced()
    {
        var (run, digests, _) = Build();
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json", Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var time = new FakeTimeProvider(Evening);
        var notices = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}"""));
        return (run, digests, BuildJob(run, digests, notices, options, time), new DigestTrigger(new CursorStore(pg.Db), options, time), notices);
    }

    [Fact]
    public async Task A_forced_run_sends_outside_the_window_once_and_clears_the_request()
    {
        await SeedAsync();
        var (_, digests, job, trigger, _) = BuildForced();

        await job.TickAsync(default);
        Assert.Empty(_sent);

        await trigger.RequestAsync(false, default);
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(3, _sent.Count);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Null(await trigger.PendingAsync(default));
        Assert.Equal("Sent.", (await trigger.LastResultAsync(default))!.Text);
    }

    [Fact]
    public async Task A_forced_run_after_a_sent_digest_says_so_and_only_resend_sends_again()
    {
        await SeedAsync();
        var (run, _, job, trigger, notices) = BuildForced();
        await run.RunAsync(Today, default);
        Assert.Equal(3, _sent.Count);

        await trigger.RequestAsync(false, default);
        await job.TickAsync(default);

        Assert.Equal(3, _sent.Count);
        Assert.Contains("already sent", Assert.Single(notices.Calls).Body, StringComparison.Ordinal);
        Assert.Null(await trigger.PendingAsync(default));

        await trigger.RequestAsync(true, default);
        await job.TickAsync(default);

        Assert.Equal(6, _sent.Count);
        Assert.Equal("Sent again.", (await trigger.LastResultAsync(default))!.Text);
    }

    [Fact]
    public async Task A_forced_run_rebuilds_a_digest_that_failed_without_items()
    {
        var (run, digests, job, trigger, _) = BuildForced();
        await run.RunAsync(Today, default);
        Assert.Equal("failed", (await digests.GetAsync(Today, default))!.Status);
        await SeedAsync();

        await trigger.RequestAsync(false, default);
        await job.TickAsync(default);

        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Equal(4, _sent.Count);   // the "no digest" notice, then header and two items
    }

    [Fact]
    public async Task A_failed_forced_run_is_reported_and_cleared_instead_of_retried_forever()
    {
        await SeedAsync();
        var (_, _, job, trigger, notices) = BuildForced();
        _failAllTelegram = true;
        await trigger.RequestAsync(false, default);

        await Assert.ThrowsAsync<TelegramException>(() => job.TickAsync(default));

        Assert.Null(await trigger.PendingAsync(default));
        Assert.StartsWith("Failed:", (await trigger.LastResultAsync(default))!.Text, StringComparison.Ordinal);
        Assert.Single(notices.Calls);
    }

    [Fact]
    public async Task A_request_from_an_earlier_day_is_dropped()
    {
        var options = Options.Create(new FeedEaterOptions());
        var cursors = new CursorStore(pg.Db);
        var time = new FakeTimeProvider(Now);
        await new DigestTrigger(cursors, options, time).RequestAsync(false, default);
        time.Advance(TimeSpan.FromDays(1));

        Assert.Null(await new DigestTrigger(cursors, options, time).PendingAsync(default));
        Assert.Null(await cursors.GetAsync("digest:force", default));
    }

    [Fact]
    public async Task The_read_prompt_carries_repository_facts_when_the_item_links_a_github_repo()
    {
        await SeedAsync();
        await LinkRepoAsync("Keep A", "Keep B");
        _githubStatus = HttpStatusCode.OK;
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Equal(2, _readPrompts.Count);
        Assert.All(_readPrompts, p => Assert.Contains("Repository facts: created 2024-03-02, last push 2026-10-01, 1,240 stars, latest release v2.3.0 on 2026-09-20", p, StringComparison.Ordinal));
        Assert.Equal(["/repos/acme/widget", "/repos/acme/widget/releases/latest"], _githubCalls);   // one lookup for both items
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Without_an_answer_from_github_the_read_goes_ahead_with_no_facts_line(HttpStatusCode status)
    {
        await SeedAsync();
        await LinkRepoAsync("Keep A");
        _githubStatus = status;
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        Assert.DoesNotContain(_readPrompts, p => p.Contains("Repository facts", StringComparison.Ordinal));
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task Items_with_no_github_link_never_call_github()
    {
        await SeedAsync();
        _githubStatus = HttpStatusCode.OK;
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Empty(_githubCalls);
        Assert.DoesNotContain(_readPrompts, p => p.Contains("Repository facts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_fetched_page_and_comments_reach_the_read_prompt_fenced_as_untrusted()
    {
        await SeedAsync();
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set extra_text = @t where title = 'Keep A'", new { t = "Linked page (pingularity.dev):\nFirst released in 2024.\n\nTop comments:\n- Solid." });
        }

        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        var prompt = Assert.Single(_readPrompts, p => p.Contains("Title: Keep A", StringComparison.Ordinal));
        Assert.Contains("untrusted_page", prompt, StringComparison.Ordinal);   // the body is JSON, so the angle brackets are escaped
        Assert.Contains("First released in 2024.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(_readPrompts, p => p.Contains("Title: Keep B", StringComparison.Ordinal) && p.Contains("First released", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_header_lists_releases_of_things_he_runs_and_a_resume_keeps_the_same_list()
    {
        await SeedAsync();
        var releases = new ReleaseStore(pg.Db);
        await releases.AddAsync(new ReleaseRow { Repo = "juanfont/headscale", Tag = "v0.30.0", Version = "0.30.0", Title = "t", Url = "https://x", Newer = true, Summary = "<b>Fixes.</b>", Breaking = "no" }, default);
        var (run, _, _) = Build();
        _failTelegramAt = 2;

        await Assert.ThrowsAsync<TelegramException>(() => run.RunAsync(Today, default));
        await releases.AddAsync(new ReleaseRow { Repo = "late/arrival", Tag = "v1.0.0", Version = "1.0.0", Title = "t", Url = "https://x", Newer = true }, default);
        await run.RunAsync(Today, default);

        var header = JsonSerializer.Deserialize<JsonElement>(_sent[0]).GetProperty("text").GetString()!;
        Assert.Contains("Updates for what you run", header, StringComparison.Ordinal);
        Assert.Contains("juanfont/headscale 0.30.0 is out (breaking: no): &lt;b&gt;Fixes.&lt;/b&gt;", header, StringComparison.Ordinal);
        Assert.DoesNotContain("late/arrival", string.Concat(_sent), StringComparison.Ordinal);
        Assert.Equal(["late/arrival"], (await releases.TakeForDigestAsync("2026-10-06", default)).Select(r => r.Repo));
    }

    [Fact]
    public async Task Switching_the_learned_taste_on_with_too_few_votes_is_refused_in_the_header_and_ranking_stays_default()
    {
        await SeedAsync();
        _learn = true;
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        var header = JsonSerializer.Deserialize<JsonElement>(_sent[0]).GetProperty("text").GetString()!;
        Assert.Contains("Learned taste is switched on but needs 100 votes", header, StringComparison.Ordinal);
        Assert.Equal(3, _sent.Count);   // the digest still went out with the default ranking
    }

    [Fact]
    public async Task Without_the_flag_the_header_says_nothing_about_learned_taste()
    {
        await SeedAsync();
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.DoesNotContain("Learned taste", _sent[0], StringComparison.Ordinal);
    }

    private static IOptions<FeedEaterOptions> QuietOptions() => Options.Create(new FeedEaterOptions
    {
        ProfilePath = "Fixtures/profile.json",
        Telegram = new TelegramOptions { AllowedUserId = 42 },
        Quiet = new FeedEater.QuietOptions { From = new TimeSpan(22, 0, 0), To = new TimeSpan(8, 0, 0) },
    });

    [Fact]
    public async Task A_due_digest_waits_for_quiet_hours_to_end_and_then_runs_once()
    {
        await SeedAsync();
        var (run, digests, _) = Build();
        var options = QuietOptions();
        var time = new FakeTimeProvider(Now);   // 07:30 Kyiv, inside 22:00 to 08:00
        var job = BuildJob(run, digests, new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""")), options, time);

        await job.TickAsync(default);
        await job.TickAsync(default);
        Assert.Empty(_sent);
        Assert.Null(await digests.GetAsync(Today, default));

        time.Advance(TimeSpan.FromMinutes(35));   // 08:05
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(3, _sent.Count);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task A_digest_asked_for_by_hand_is_not_held_by_quiet_hours()
    {
        await SeedAsync();
        var (run, digests, _) = Build();
        var options = QuietOptions();
        var time = new FakeTimeProvider(Now);
        var job = BuildJob(run, digests, new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""")), options, time);
        await new DigestTrigger(new CursorStore(pg.Db), options, time).RequestAsync(false, default);

        await job.TickAsync(default);

        Assert.Equal(3, _sent.Count);
    }
}
