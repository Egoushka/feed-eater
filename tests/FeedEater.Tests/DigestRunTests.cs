using Dapper;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Inbox;
using FeedEater.Llm;
using FeedEater.Signals;
using FeedEater.Loops;
using FeedEater.Push;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DigestRunTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Today = "2026-10-05";
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 4, 30, 0, TimeSpan.Zero); // 07:30 Kyiv
    private static readonly string LongText = string.Join(' ', Enumerable.Repeat("Postgres async I/O details.", 80));

    private readonly List<(string Title, string? Click)> _pushes = [];
    private readonly LoopHealth _health = new(new FakeTimeProvider(Now));
    private int _chatCalls;
    private bool _budgetOnB;
    private bool _readThrows;
    private int _triageCalls;
    private bool _llmDown;
    private bool _readsDown;
    private bool _rateLimitFirstTriage;
    private bool _vetoA;
    private bool _marginalA;
    private bool _vetoAll;
    private int _readCap = 12;
    private bool _learn;
    private string _profilePath = "Fixtures/profile.json";
    private bool _miniflux = true;
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
            if (_rateLimitFirstTriage)
            {
                _rateLimitFirstTriage = false;
                var limited = StubHandler.Json(
                    """{"error":{"message":"token limit exceeded (600000/600000, resets every 1h)"}}""", HttpStatusCode.TooManyRequests);
                limited.Headers.Add("Retry-After", "0");
                return limited;
            }

            var keep = body.Contains("Title: Keep", StringComparison.Ordinal);
            return StubHandler.Json(Chat(keep ? """{"relevance":3,"project":"homelab","kind":"improve","reason":"r"}""" : """{"relevance":0,"kind":"fyi"}"""));
        }

        if (_readThrows)
        {
            throw new InvalidOperationException("read model exploded");
        }

        _readPrompts.Add(body);

        if (_vetoAll)
        {
            return StubHandler.Json(Chat("""{"relevance":0,"summary":"A beginner question.","why":"Not relevant.","kind":"fyi","project":null,"suggestion":null}"""));
        }

        if (_marginalA && body.Contains("Title: Keep A", StringComparison.Ordinal))
        {
            return StubHandler.Json(Chat("""{"relevance":1,"summary":"A is a showcase.","why":"Loosely related.","kind":"new","project":"homelab","suggestion":"Turn A on."}"""));
        }

        if (_vetoA && body.Contains("Title: Keep A", StringComparison.Ordinal))
        {
            return StubHandler.Json(Chat("""{"relevance":0,"summary":"A is a beginner question.","why":"Not relevant.","kind":"fyi","project":null,"suggestion":null}"""));
        }

        if (body.Contains("Title: Keep A", StringComparison.Ordinal))
        {
            return StubHandler.Json(Chat("""{"summary":"A is new.","why":"Box runs it.","kind":"improve","project":"homelab","suggestion":"Turn A on."}"""));
        }

        return _budgetOnB
            ? StubHandler.Json("""{"error":{"message":"ExceededTokenBudget"}}""", HttpStatusCode.Unauthorized)
            : StubHandler.Json(Chat("""{"summary":"B is out.","why":"Context.","kind":"fyi","project":null,"suggestion":null}"""));
    }

    private (DigestRun Run, DigestStore Digests, StubHandler Miniflux) Build()
    {
        var options = Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv", ProfilePath = _profilePath, Miniflux = _miniflux ? new MinifluxOptions { BaseUrl = "http://miniflux/", Token = "t" } : new MinifluxOptions(), Push = new PushOptions { NtfyUrl = "http://ntfy/", Topic = "feed", BaseUrl = "https://feed.example" }, Taste = new TasteOptions { Learn = _learn }, Caps = new CapsOptions { Read = _readCap } });
        var time = new FakeTimeProvider(Now);
        var miniflux = new StubHandler((_, _) => StubHandler.Json(JsonSerializer.Serialize(new { content = $"<p>{LongText}</p>" })));
        var llm = new StubHandler((_, body) => body.Contains("\"input\"", StringComparison.Ordinal)
            ? StubHandler.Json(TestVectors.EmbeddingResponse(1))
            : Llm(body));
        var ntfy = new StubHandler((request, _) =>
        {
            _pushes.Add((request.Headers.GetValues("Title").Single(), request.Headers.TryGetValues("Click", out var click) ? click.Single() : null));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var digests = new DigestStore(pg.Db);
        var run = new DigestRun(
            new ItemStore(pg.Db), new ReleaseStore(pg.Db), new ProfileStore(pg.Db), new FeedbackStore(pg.Db), new AnalysisStore(pg.Db), digests,
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new FeedEater.Sources.ItemText(new ItemStore(pg.Db), options, new MinifluxClient(miniflux.Client("http://miniflux/")), NullLogger<FeedEater.Sources.ItemText>.Instance),
            new GitHubStarsClient(new StubHandler((request, _) => GitHub(request)).Client("http://github/"), options),
            PushKit.Notifier(pg, options, time, ntfy),
            new InboxService(new InboxStore(pg.Db), time), new InboxStore(pg.Db),
            _health, new FeedEater.Ranking.TasteSwitch(new CursorStore(pg.Db), new FeedbackStore(pg.Db), options), options, time, NullLogger<DigestRun>.Instance);
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

    private DigestJob BuildJob(DigestRun run, DigestStore digests, IOptions<FeedEaterOptions> options, FakeTimeProvider time) =>
        new(run, digests, new InboxService(new InboxStore(pg.Db), time), new DigestTrigger(new CursorStore(pg.Db), options, time),
            new QuietHours(new CursorStore(pg.Db), options, time), new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<DigestJob>.Instance);

    private async Task SeedAsync()
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [new Profile { Key = "homelab", Kind = "project", Repo = "octo/homelab", Description = "Hetzner VPS.", Embedding = TestVectors.OneHot(0) }],
            Now, default);
        var hourAgo = Now.UtcDateTime.AddHours(-1);
        await Seed.ItemAsync(pg, 1, "Keep A", TestVectors.OneHot(0), hourAgo, hourAgo, LongText, 201);
        await Seed.ItemAsync(pg, 1, "Keep B", TestVectors.Blend(0, 1, 0.3f), hourAgo, hourAgo, "short", 202);
        await Seed.ItemAsync(pg, 2, "Drop C", TestVectors.Blend(0, 2, 0.6f), hourAgo, hourAgo, LongText, 203);
        // Backfilled today but published a month ago: archived, never a candidate.
        await Seed.ItemAsync(pg, 2, "Keep Old", TestVectors.OneHot(0), Now.UtcDateTime.AddDays(-30), hourAgo, LongText, 204);
    }

    /// <summary>The titles of the digest's items, in the order the digest shows them.</summary>
    private async Task<List<string>> ShownAsync()
    {
        var ids = (await new DigestStore(pg.Db).GetAsync(Today, default))?.ItemIds ?? [];
        var titles = new List<string>();
        foreach (var id in ids)
        {
            titles.Add((await ScalarAsync<string>("select title from items where id = @id", new { id }))!);
        }

        return titles;
    }

    private async Task<string> NoteAsync() => (await new DigestStore(pg.Db).GetAsync(Today, default))?.Note ?? "";

    private Task<int> NoticesAsync(string like) => ScalarAsync<int>("select count(*)::int from inbox where kind = 'notice' and key like @like", new { like })!;

    private async Task<T?> ScalarAsync<T>(string sql, object args)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task The_digest_is_selected_and_marked_sent_for_the_stack()
    {
        await SeedAsync();
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        var d = await digests.GetAsync(Today, default);
        Assert.Equal("sent", d!.Status);
        Assert.Equal(2, d.ItemIds.Length);
        Assert.Equal(d.ItemIds.Length, d.SentCount);
    }

    [Fact]
    public async Task A_sent_digest_pushes_one_ntfy_message_linking_the_today_page()
    {
        await SeedAsync();
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);
        await run.RunAsync(Today, default);

        Assert.Equal(("Today: 2 items", "https://feed.example/ui"), Assert.Single(_pushes));
    }

    [Fact]
    public async Task No_digest_today_is_a_notice_card_and_no_push()
    {
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);
        await run.RunAsync(Today, default);

        Assert.Equal("notice", await ScalarAsync<string>("select kind from inbox where key = @k", new { k = $"notice:digest:{Today}:none" }));
        Assert.Equal("failed", (await digests.GetAsync(Today, default))!.Status);
        Assert.Empty(_pushes);
    }

    [Fact]
    public async Task Selection_notes_are_stored_on_the_digest_and_as_a_notice()
    {
        await SeedAsync();
        _health.Failed(Ingestor.LoopName, "refused");
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Contains("unreachable", await ScalarAsync<string>("select payload->>'text' from inbox where key = @k", new { k = $"notice:digest:{Today}:notes" }), StringComparison.Ordinal);
        Assert.Contains("unreachable", await NoteAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selects_ideas_first_and_runs_only_once()
    {
        await SeedAsync();
        var (run, digests, miniflux) = Build();

        await run.RunAsync(Today, default);
        var chatCalls = _chatCalls;
        await run.RunAsync(Today, default);

        Assert.Equal(["Keep A", "Keep B"], await ShownAsync());
        Assert.Equal(chatCalls, _chatCalls);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Single(miniflux.Calls, c => c.Uri.Contains("/v1/entries/202/fetch-content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_the_reader_model_vetoes_after_reading_is_dropped_and_the_next_candidate_takes_its_slot()
    {
        _vetoA = true;
        _readCap = 1;
        await SeedAsync();
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Equal(["Keep B"], await ShownAsync());
    }

    [Fact]
    public async Task An_item_the_reader_model_calls_marginal_is_kept_after_the_ones_worth_reading()
    {
        _marginalA = true;
        await SeedAsync();
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Equal(["Keep B", "Keep A"], await ShownAsync());
    }

    [Fact]
    public async Task When_the_reader_model_vetoes_every_item_the_no_digest_notice_says_so()
    {
        _vetoAll = true;
        await SeedAsync();
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        var text = await ScalarAsync<string>("select payload->>'text' from inbox where key = @k", new { k = $"notice:digest:{Today}:none" });
        Assert.Contains("No digest today", text, StringComparison.Ordinal);
        Assert.Contains("the reader model rated all 2 items it read below 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_profile_file_uses_the_example_interests_and_the_note_says_so()
    {
        _profilePath = "/nope/profile.json";
        await SeedAsync();
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Contains("Using the example interests; edit /nope/profile.json", await NoteAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_Miniflux_the_read_uses_the_feed_text_and_never_calls_it()
    {
        await SeedAsync();
        _miniflux = false;
        var (run, _, miniflux) = Build();

        await run.RunAsync(Today, default);

        Assert.Empty(miniflux.Calls);
        Assert.Equal(2, (await ShownAsync()).Count);
    }

    [Fact]
    public async Task An_embedding_outage_is_not_reported_as_a_Miniflux_outage()
    {
        await SeedAsync();
        _health.Failed(Ingestor.EmbedName, "LiteLLM down");
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);
        Assert.Contains("Embeddings were unavailable", await NoteAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("Miniflux was unreachable", await NoteAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Miniflux_outage_is_reported_as_one()
    {
        await SeedAsync();
        _health.Failed(Ingestor.LoopName, "refused");
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);
        Assert.Contains("Miniflux was unreachable", await NoteAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("Embeddings were unavailable", await NoteAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_job_run_keeps_the_digest_retryable_and_reports_once()
    {
        await SeedAsync();
        var options = Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv", ProfilePath = "Fixtures/profile.json" });
        var (run, digests, _) = Build();
        var time = new FakeTimeProvider(Now);
        var job = BuildJob(run, digests, options, time);
        _readThrows = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));

        var row = (await digests.GetAsync(Today, default))!;
        Assert.NotEqual("failed", row.Status);
        Assert.NotNull(row.Error);
        Assert.Equal(1, await NoticesAsync("notice:digest:%:failed"));
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
        Assert.Equal(["Keep A", "Keep B"], await ShownAsync());
    }

    [Fact]
    public async Task A_spent_budget_still_keeps_what_was_read_with_a_note()
    {
        await SeedAsync();
        _budgetOnB = true;
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Equal(["Keep A"], await ShownAsync());
        Assert.Contains("budget reached", await NoteAsync(), StringComparison.Ordinal);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task No_candidates_leaves_one_explanation_and_returns()
    {
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);
        await run.RunAsync(Today, default);

        Assert.Equal(1, await NoticesAsync("notice:digest:%:none"));
        Assert.Equal("failed", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task A_litellm_outage_during_triage_throws_so_the_job_retries_and_a_rerun_sends()
    {
        await SeedAsync();
        _llmDown = true;
        var (run, digests, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(Today, default));

        Assert.Empty(_pushes);
        Assert.NotEqual("failed", (await digests.GetAsync(Today, default))!.Status);

        _llmDown = false;
        await run.RunAsync(Today, default);

        Assert.Equal(2, (await ShownAsync()).Count);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task A_429_token_limit_during_triage_waits_then_completes_and_the_digest_is_sent()
    {
        await SeedAsync();
        _rateLimitFirstTriage = true;
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        var digest = (await digests.GetAsync(Today, default))!;
        Assert.Equal("sent", digest.Status);
        Assert.Contains("waited for the token limit to reset", digest.Note ?? "");
    }

    [Fact]
    public async Task A_litellm_outage_during_reads_throws_and_a_rerun_sends_without_new_triage()
    {
        await SeedAsync();
        _readsDown = true;
        var (run, digests, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(Today, default));

        Assert.Empty(_pushes);
        Assert.NotEqual("failed", (await digests.GetAsync(Today, default))!.Status);

        _readsDown = false;
        var triageBefore = _triageCalls;
        await run.RunAsync(Today, default);

        Assert.Equal(triageBefore, _triageCalls);
        Assert.Equal(2, (await ShownAsync()).Count);
    }

    [Fact]
    public async Task A_new_days_run_closes_older_building_digests_as_failed()
    {
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, error, note) values ('2026-10-04', 'building', 'LLM 429', null), ('2026-10-03', 'sent', null, null), ('2026-10-02', 'building', null, 'a note')");
        }

        var (run, digests, _) = Build();
        await run.RunAsync(Today, default);

        var stale = (await digests.GetAsync("2026-10-04", default))!;
        Assert.Equal("failed", stale.Status);
        Assert.Equal("LLM 429", stale.Error);
        Assert.Contains("not sent before 12:00", stale.Note, StringComparison.Ordinal);
        var noted = (await digests.GetAsync("2026-10-02", default))!;
        Assert.Equal("failed", noted.Status);
        Assert.Equal("a note\nnot sent before 12:00", noted.Note);
        Assert.Equal("not sent before 12:00", noted.Error);
        Assert.Equal("sent", (await digests.GetAsync("2026-10-03", default))!.Status);
    }

    private static readonly DateTimeOffset Evening = Now.AddHours(10);   // 17:30 Kyiv: outside the window

    private (DigestRun Run, DigestStore Digests, DigestJob Job, DigestTrigger Trigger) BuildForced()
    {
        var (run, digests, _) = Build();
        var options = Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv", ProfilePath = "Fixtures/profile.json" });
        var time = new FakeTimeProvider(Evening);
        return (run, digests, BuildJob(run, digests, options, time), new DigestTrigger(new CursorStore(pg.Db), options, time));
    }

    private async Task<string?> NoticeTextAsync(string suffix) =>
        await ScalarAsync<string>("select payload->>'text' from inbox where key = @k", new { k = $"notice:digest:{Today}:{suffix}" });

    [Fact]
    public async Task A_forced_run_builds_outside_the_window_once_and_clears_the_request()
    {
        await SeedAsync();
        var (_, digests, job, trigger) = BuildForced();

        await job.TickAsync(default);
        Assert.Null(await digests.GetAsync(Today, default));

        await trigger.RequestAsync(false, default);
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(2, (await ShownAsync()).Count);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Null(await trigger.PendingAsync(default));
        Assert.Equal("Sent.", (await trigger.LastResultAsync(default))!.Text);
    }

    [Fact]
    public async Task A_forced_run_after_a_sent_digest_says_so_and_only_resend_runs_the_send_again()
    {
        await SeedAsync();
        var (run, digests, job, trigger) = BuildForced();
        await run.RunAsync(Today, default);

        await trigger.RequestAsync(false, default);
        await job.TickAsync(default);

        Assert.Contains("already sent", await NoticeTextAsync("notice"), StringComparison.Ordinal);
        Assert.Null(await trigger.PendingAsync(default));

        await trigger.RequestAsync(true, default);
        await job.TickAsync(default);

        Assert.Equal("Sent again.", (await trigger.LastResultAsync(default))!.Text);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Single(_pushes);   // the push message is once per digest, not per resend
    }

    [Fact]
    public async Task A_notice_that_cannot_be_stored_does_not_fail_the_forced_run()
    {
        await SeedAsync();
        var (run, _, job, trigger) = BuildForced();
        await run.RunAsync(Today, default);
        await trigger.RequestAsync(false, default);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("alter table inbox rename to inbox_away");
        try
        {
            await job.TickAsync(default);
        }
        finally
        {
            await c.ExecuteAsync("alter table inbox_away rename to inbox");
        }

        Assert.Null(await trigger.PendingAsync(default));
        Assert.Contains("already sent", (await trigger.LastResultAsync(default))!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forced_run_rebuilds_a_digest_that_failed_without_items()
    {
        var (run, digests, job, trigger) = BuildForced();
        await run.RunAsync(Today, default);
        Assert.Equal("failed", (await digests.GetAsync(Today, default))!.Status);
        await SeedAsync();

        await trigger.RequestAsync(false, default);
        await job.TickAsync(default);

        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Equal(2, (await ShownAsync()).Count);
    }

    [Fact]
    public async Task A_failed_forced_run_is_reported_and_cleared_instead_of_retried_forever()
    {
        await SeedAsync();
        var (_, _, job, trigger) = BuildForced();
        _readThrows = true;
        await trigger.RequestAsync(false, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));

        Assert.Null(await trigger.PendingAsync(default));
        Assert.StartsWith("Failed:", (await trigger.LastResultAsync(default))!.Text, StringComparison.Ordinal);
        Assert.Equal(1, await NoticesAsync("notice:digest:%:failed"));
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
    public async Task A_sent_digest_claims_the_releases_of_things_he_runs_and_later_ones_wait()
    {
        await SeedAsync();
        var releases = new ReleaseStore(pg.Db);
        await releases.AddAsync(new ReleaseRow { Repo = "juanfont/headscale", Tag = "v0.30.0", Version = "0.30.0", Title = "t", Url = "https://x", Newer = true, Summary = "Fixes.", Breaking = "no" }, default);
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);
        await releases.AddAsync(new ReleaseRow { Repo = "late/arrival", Tag = "v1.0.0", Version = "1.0.0", Title = "t", Url = "https://x", Newer = true }, default);

        Assert.Equal(["juanfont/headscale"], (await releases.TakeForDigestAsync(Today, default)).Select(r => r.Repo));
        Assert.Equal(["late/arrival"], (await releases.TakeForDigestAsync("2026-10-06", default)).Select(r => r.Repo));
    }

    [Fact]
    public async Task Switching_the_learned_taste_on_with_too_few_votes_is_refused_in_the_note_and_ranking_stays_default()
    {
        await SeedAsync();
        _learn = true;
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Contains("Learned taste is switched on but needs 100 votes", await NoteAsync(), StringComparison.Ordinal);
        Assert.Equal(2, (await ShownAsync()).Count);   // the digest still went out with the default ranking
    }

    [Fact]
    public async Task Without_the_flag_the_note_says_nothing_about_learned_taste()
    {
        await SeedAsync();
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.DoesNotContain("Learned taste", await NoteAsync(), StringComparison.Ordinal);
    }

    private static IOptions<FeedEaterOptions> QuietOptions() => Options.Create(new FeedEaterOptions
    {
        TimeZone = "Europe/Kyiv",
        ProfilePath = "Fixtures/profile.json",
        Quiet = new FeedEater.QuietOptions { From = new TimeSpan(22, 0, 0), To = new TimeSpan(8, 0, 0) },
    });

    [Fact]
    public async Task A_due_digest_waits_for_quiet_hours_to_end_and_then_runs_once()
    {
        await SeedAsync();
        var (run, digests, _) = Build();
        var options = QuietOptions();
        var time = new FakeTimeProvider(Now);   // 07:30 Kyiv, inside 22:00 to 08:00
        var job = BuildJob(run, digests, options, time);

        await job.TickAsync(default);
        await job.TickAsync(default);
        Assert.Empty(_pushes);
        Assert.Null(await digests.GetAsync(Today, default));

        time.Advance(TimeSpan.FromMinutes(35));   // 08:05
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(2, (await ShownAsync()).Count);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Single(_pushes);
    }

    [Fact]
    public async Task A_digest_asked_for_by_hand_is_not_held_by_quiet_hours()
    {
        await SeedAsync();
        var (run, digests, _) = Build();
        var options = QuietOptions();
        var time = new FakeTimeProvider(Now);
        var job = BuildJob(run, digests, options, time);
        await new DigestTrigger(new CursorStore(pg.Db), options, time).RequestAsync(false, default);

        await job.TickAsync(default);

        Assert.Equal(2, (await ShownAsync()).Count);
    }
}
