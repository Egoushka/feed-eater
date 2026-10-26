using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Inbox;
using FeedEater.Issues;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.WeeklyIdeas;

namespace FeedEater.Tests;

public sealed class IdeaShortlistTests
{
    private static readonly string[] Projects = ["octo/homelab", "octo/chargehand"];

    [Fact]
    public void Reads_ideas_by_lead_number_and_drops_what_does_not_fit()
    {
        var reply = """
            Here you go: {"ideas": [
              {"lead": 2, "idea": "Cache the thing", "project": "chargehand", "where": "chargehand", "effort": "s", "why": "Faster."},
              {"lead": 2, "idea": "Same lead twice", "project": "octo/homelab"},
              {"lead": 9, "idea": "No such lead", "project": "octo/homelab"},
              {"lead": 1, "idea": "Bump X", "project": "ELSEWHERE", "effort": "huge"},
              {"lead": 3, "project": "octo/homelab"}
            ]}
            """;

        var entries = IdeaShortlist.Parse(reply, 3, Projects, "octo/homelab");

        Assert.Equal(
            [(1, "Cache the thing", "octo/chargehand", "S"), (0, "Bump X", "octo/homelab", "M")],
            entries.Select(e => (e.Lead, e.Idea, e.Project, e.Effort)));
    }

    [Theory]
    [InlineData("no json")]
    [InlineData("{\"ideas\": 5}")]
    [InlineData("{\"ideas\": [")]
    public void An_unreadable_reply_gives_no_ideas(string reply) =>
        Assert.Empty(IdeaShortlist.Parse(reply, 3, Projects, "octo/homelab"));
}

[Collection(PostgresCollection.Name)]
public sealed class WeeklyIdeasJobTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 7, 0, 0, TimeSpan.Zero);   // 10:00 Kyiv
    private static readonly DateTime Seeded = Monday.UtcDateTime.AddDays(-1);   // inside Monday's window, outside next Monday's, whatever the real date

    private string _chatReply = "";
    private string? _backlog = "| AIS-1 | Existing row |";
    private readonly List<string> _chatBodies = [];
    private bool _githubDown;
    private ManualResetEventSlim? _githubGate;
    private readonly ManualResetEventSlim _githubEntered = new();

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (WeeklyIdeasJob Job, StubHandler GitHub, StubHandler Telegram) Build(TimeProvider time, string backlogUrl = "http://backlog/file")
    {
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Telegram = new TelegramOptions { AllowedUserId = 42, Token = "T" },
            GitHub = new GitHubOptions { Token = "t", FallbackRepo = "octo/feed" },
            WeeklyIdeas = new WeeklyIdeasOptions { BacklogUrl = backlogUrl },
        });
        var github = new StubHandler((request, _) => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/repos/octo/homelab/issues") => StubHandler.Json("""[{"title":"Existing issue"},{"title":"A pull request","pull_request":{}}]"""),
            ("GET", "/repos/octo/feed/issues") => StubHandler.Json("{}", HttpStatusCode.NotFound),
            ("POST", _) when _githubDown => StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable),
            ("POST", _) when Gate() => StubHandler.Json("""{"number":11}""", HttpStatusCode.Created),
            _ => StubHandler.Json("{}", HttpStatusCode.NotFound),
        });
        var llm = new StubHandler((_, body) =>
        {
            _chatBodies.Add(body);
            return StubHandler.Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = _chatReply } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } }));
        });
        var telegram = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}"""));
        var backlog = new StubHandler((_, _) => _backlog is null ? StubHandler.Json("{}", HttpStatusCode.BadGateway) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_backlog) });
        var cursors = new CursorStore(pg.Db);
        var job = new WeeklyIdeasJob(
            new WeeklyIdeaStore(pg.Db), new ProfileStore(pg.Db), new GitHubIssues(github.Client("http://github/")),
            new BacklogClient(backlog.Client("http://backlog/"), options),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new TelegramClient(telegram.Client("http://tg/botT/")), new InboxStore(pg.Db), new QuietHours(cursors, options, time),
            cursors, options, new LoopHealth(time), time, NullLogger<WeeklyIdeasJob>.Instance);
        return (job, github, telegram);
    }

    private bool Gate()
    {
        _githubEntered.Set();
        _githubGate?.Wait();
        return true;
    }

    private IdeaDecisions Decisions(StubHandler github, TimeProvider time)
    {
        var options = Options.Create(new FeedEaterOptions { GitHub = new GitHubOptions { Token = "t", FallbackRepo = "octo/feed" } });
        return new IdeaDecisions(new InboxStore(pg.Db), new WeeklyIdeaStore(pg.Db), new GitHubIssues(github.Client("http://github/")), time);
    }

    private async Task<(IdeaDecisions Decisions, StubHandler GitHub, long InboxId)> StagedAsync()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"Turn on caching","project":"octo/homelab","where":"homelab-gitops","effort":"S","why":"Saves requests."}]}""";
        var time = new FakeTimeProvider(Monday);
        var (job, github, _) = Build(time);
        await job.TickAsync(default);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<long>("select id from inbox where kind = 'idea'");
        return (Decisions(github, time), github, id);
    }

    private async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    private async Task<string> SeedAsync()
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync([new Profile { Key = "homelab", Kind = "project", Repo = "octo/homelab", Description = "The box", Embedding = TestVectors.OneHot(0) }], DateTimeOffset.UtcNow, default);
        var improve = await Seed.ItemAsync(pg, 1, "Tool A adds caching", TestVectors.OneHot(1), publishedAt: Seeded);
        await Seed.ReadAsync(pg, improve, "homelab", "improve");
        var liked = await Seed.ItemAsync(pg, 1, "Tool B showcase", TestVectors.OneHot(2));
        await new FeedbackStore(pg.Db).SetVoteAsync(liked, 1, default);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update votes set at = @at", new { at = Seeded });
        await c.ExecuteAsync("update reads set suggestion = 'Turn on caching in the box.' where item_id = @improve", new { improve });
        await c.ExecuteAsync("insert into signals (source, external_id, url, title, embedding, at) values ('karakeep', 'k1', 'https://example.org/bookmarked', 'A bookmarked tool', @e::real[]::vector, @at)", new { e = TestVectors.OneHot(3), at = Seeded });
        return await c.ExecuteScalarAsync<string>("select url from items where id = @improve", new { improve }) ?? "";
    }

    [Fact]
    public async Task The_shortlist_stages_ideas_and_files_nothing_and_the_top_go_to_Telegram_with_buttons()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"Turn on caching","project":"octo/homelab","where":"homelab-gitops","effort":"S","why":"Saves requests."}]}""";
        var (job, github, telegram) = Build(new FakeTimeProvider(Monday));

        await job.TickAsync(default);

        Assert.DoesNotContain(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from inbox where kind = 'idea'"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from weekly_ideas"));
        Assert.Contains("homelab-gitops", await ScalarAsync<string>("select payload->>'body' from inbox where kind = 'idea'"), StringComparison.Ordinal);
        Assert.Equal("octo/homelab", await ScalarAsync<string>("select payload->>'project' from inbox where kind = 'idea'"));
        var message = Assert.Single(telegram.Calls);
        Assert.Contains("Ideas for 2026-W43", message.Body, StringComparison.Ordinal);
        Assert.Contains("Turn on caching", message.Body, StringComparison.Ordinal);
        Assert.Contains("ik:", message.Body, StringComparison.Ordinal);
        Assert.Contains("id:", message.Body, StringComparison.Ordinal);
        Assert.Contains("AIS-1", _chatBodies.Single(), StringComparison.Ordinal);            // the backlog reached the model
        Assert.Contains("octo/homelab: Existing issue", _chatBodies.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("A pull request", _chatBodies.Single(), StringComparison.Ordinal);   // and octo/feed's 404 only skips its issues
        Assert.Contains("A bookmarked tool", _chatBodies.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_staged_idea_gets_Keep_and_Drop_buttons_and_only_the_top_are_detailed()
    {
        var staged = Enumerable.Range(1, 5).Select(i => (new ShortlistEntry(i, $"Idea {i}", "LAB", "box", "S", $"Why {i}"), new Lead("improve", $"Source {i}", $"https://e.org/{i}", ""), (long)i)).ToList();

        var message = WeeklyIdeasJob.Message("2026-W43", staged, 3);

        Assert.Equal(5, message.Keyboard!.Count);
        Assert.Equal(["ik:4", "id:4"], message.Keyboard[3].Select(b => b.Data));
        Assert.Contains("4. Idea 4", message.Html, StringComparison.Ordinal);
        Assert.Contains("5. Idea 5", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Why 4", message.Html, StringComparison.Ordinal);
        Assert.Contains("Why 3", message.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keep_with_the_local_sink_answers_Filed_in_the_repo_not_Saved_as_an_idea()
    {
        var (decisions, _, inboxId) = await StagedAsync();
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { AllowedUserId = 42, Token = "T" } });
        var telegram = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":true}"""));
        var client = new TelegramClient(telegram.Client("http://tg/botT/"));
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var filer = new IdeaFiler(items, feedback, new ProfileStore(pg.Db), new LocalIdeaSink(), TimeProvider.System);
        var callbacks = new CallbackHandler(client, feedback, filer, items, new DuelStore(pg.Db),
            Seed.Library(pg, options), options, NullLogger<CallbackHandler>.Instance, decisions: decisions);

        await callbacks.HandleAsync(new TgCallback("cb1", 42, 42, 1, CallbackData.KeepIdea(inboxId)), default);

        var answer = Assert.Single(telegram.Calls).Body;
        Assert.Contains("Filed in octo/homelab", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Saved as an idea", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keep_files_once_and_a_second_keep_is_already_decided()
    {
        var (decisions, github, inboxId) = await StagedAsync();

        var first = await decisions.KeepAsync(inboxId, default);
        var second = await decisions.KeepAsync(inboxId, default);

        Assert.Equal(IdeaOutcome.Filed, first.Outcome);
        Assert.Equal("octo/homelab", first.Project);
        Assert.Equal(11, await ScalarAsync<int>("select issue_number from weekly_ideas"));
        Assert.Equal(IdeaOutcome.AlreadyDecided, second.Outcome);
        Assert.Single(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("filed", await ScalarAsync<string>("select outcome from weekly_ideas"));
    }

    [Fact]
    public async Task Concurrent_presses_while_a_Keep_is_in_GitHub_are_already_decided_and_one_item_is_filed()
    {
        var (decisions, github, inboxId) = await StagedAsync();
        _githubGate = new ManualResetEventSlim();
        var first = Task.Run(() => decisions.KeepAsync(inboxId, default));
        Assert.True(_githubEntered.Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal(IdeaOutcome.AlreadyDecided, (await decisions.KeepAsync(inboxId, default)).Outcome);
        Assert.Equal(IdeaOutcome.AlreadyDecided, (await decisions.DropAsync(inboxId, default)).Outcome);
        _githubGate.Set();

        Assert.Equal(IdeaOutcome.Filed, (await first).Outcome);
        Assert.Single(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from weekly_ideas where outcome = 'filed'"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from weekly_ideas"));
    }

    [Fact]
    public async Task An_open_idea_staged_again_next_Monday_stays_one_row()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":3,"idea":"Try the bookmarked tool","project":"octo/homelab"}]}""";   // lead 3 is the bookmark
        var time = new FakeTimeProvider(Monday);
        var (job, _, _) = Build(time);

        await job.TickAsync(default);
        time.Advance(TimeSpan.FromDays(7));
        _chatReply = """{"ideas":[{"lead":1,"idea":"Try the bookmarked tool","project":"octo/homelab"}]}""";   // the week-old items are out of the window; the bookmark is lead 1
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update signals set at = @at where source = 'karakeep'", new { at = time.GetUtcNow().UtcDateTime });
        }

        await job.TickAsync(default);

        Assert.Equal(2, _chatBodies.Count);   // both Mondays ran
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from inbox where kind = 'idea'"));
        Assert.Equal("2026-W43", await ScalarAsync<string>("select payload->>'week' from inbox where kind = 'idea'"));
    }

    [Fact]
    public async Task Drop_remembers_the_source_and_files_nothing()
    {
        var (decisions, github, inboxId) = await StagedAsync();

        Assert.Equal(IdeaOutcome.Dropped, (await decisions.DropAsync(inboxId, default)).Outcome);
        Assert.Equal(IdeaOutcome.AlreadyDecided, (await decisions.KeepAsync(inboxId, default)).Outcome);
        Assert.Equal(IdeaOutcome.NotFound, (await decisions.KeepAsync(inboxId + 1000, default)).Outcome);
        Assert.DoesNotContain(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("dropped", await ScalarAsync<string>("select outcome from weekly_ideas"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from weekly_ideas where issue_number is null"));
    }

    [Fact]
    public async Task Keep_while_GitHub_is_down_leaves_the_idea_open()
    {
        var (decisions, _, inboxId) = await StagedAsync();
        _githubDown = true;

        Assert.Equal(IdeaOutcome.Failed, (await decisions.KeepAsync(inboxId, default)).Outcome);
        Assert.Null(await ScalarAsync<DateTime?>("select done_at from inbox where id = @inboxId", new { inboxId }));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from weekly_ideas"));

        _githubDown = false;
        Assert.Equal(IdeaOutcome.Filed, (await decisions.KeepAsync(inboxId, default)).Outcome);
    }

    [Fact]
    public async Task A_source_filed_before_is_not_offered_again_and_a_run_with_no_leads_asks_nothing()
    {
        var url = await SeedAsync();
        await new WeeklyIdeaStore(pg.Db).AddAsync(new FiledIdea(FeedEater.Text.UrlCanonicalizer.Canonical(url), "2026-W42", "Earlier", "octo/homelab", 1), default);
        _chatReply = """{"ideas":[]}""";
        var (job, github, telegram) = Build(new FakeTimeProvider(Monday));

        await job.TickAsync(default);

        Assert.DoesNotContain("Tool A adds caching", _chatBodies.Single(), StringComparison.Ordinal);
        Assert.Contains("Tool B showcase", _chatBodies.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Empty(telegram.Calls);

        await pg.ResetAsync();
        _chatBodies.Clear();
        var (empty, _, _) = Build(new FakeTimeProvider(Monday));
        await empty.TickAsync(default);
        Assert.Empty(_chatBodies);
    }

    [Fact]
    public async Task An_unreadable_backlog_fails_the_run_so_nothing_is_filed_without_it()
    {
        await SeedAsync();
        _backlog = null;
        _chatReply = """{"ideas":[{"lead":1,"idea":"X","project":"octo/homelab"}]}""";
        var (job, github, _) = Build(new FakeTimeProvider(Monday));

        await Assert.ThrowsAsync<HttpRequestException>(() => job.TickAsync(default));

        Assert.DoesNotContain(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Empty(_chatBodies);
        Assert.Null(await new CursorStore(pg.Db).GetAsync("job:weekly-ideas", default));
    }

    [Fact]
    public async Task Runs_once_per_Monday()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"X","project":"octo/homelab"}]}""";
        var (job, _, telegram) = Build(new FakeTimeProvider(Monday));

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Single(telegram.Calls);
        Assert.Equal("2026-10-19", await new CursorStore(pg.Db).GetAsync("job:weekly-ideas", default));
    }

    [Fact]
    public async Task A_force_request_runs_again_once_and_is_cleared()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"X","project":"octo/homelab"}]}""";
        var (job, _, telegram) = Build(new FakeTimeProvider(Monday));
        var cursors = new CursorStore(pg.Db);

        await job.TickAsync(default);
        await cursors.SetAsync(WeeklyIdeasJob.ForceCursor, "1", default);
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(2, telegram.Calls.Count);
        Assert.Null(await cursors.GetAsync(WeeklyIdeasJob.ForceCursor, default));
    }
}
