using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Inbox;
using FeedEater.Plane;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.WeeklyIdeas;

namespace FeedEater.Tests;

public sealed class IdeaShortlistTests
{
    private static readonly string[] Projects = ["LAB", "CHARGEHAND"];

    [Fact]
    public void Reads_ideas_by_lead_number_and_drops_what_does_not_fit()
    {
        var reply = """
            Here you go: {"ideas": [
              {"lead": 2, "idea": "Cache the thing", "project": "chargehand", "where": "chargehand", "effort": "s", "why": "Faster."},
              {"lead": 2, "idea": "Same lead twice", "project": "LAB"},
              {"lead": 9, "idea": "No such lead", "project": "LAB"},
              {"lead": 1, "idea": "Bump X", "project": "ELSEWHERE", "effort": "huge"},
              {"lead": 3, "project": "LAB"}
            ]}
            """;

        var entries = IdeaShortlist.Parse(reply, 3, Projects, "LAB");

        Assert.Equal(
            [(1, "Cache the thing", "CHARGEHAND", "S"), (0, "Bump X", "LAB", "M")],
            entries.Select(e => (e.Lead, e.Idea, e.Project, e.Effort)));
    }

    [Theory]
    [InlineData("no json")]
    [InlineData("{\"ideas\": 5}")]
    [InlineData("{\"ideas\": [")]
    public void An_unreadable_reply_gives_no_ideas(string reply) =>
        Assert.Empty(IdeaShortlist.Parse(reply, 3, Projects, "LAB"));
}

[Collection(PostgresCollection.Name)]
public sealed class WeeklyIdeasJobTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 7, 0, 0, TimeSpan.Zero);   // 10:00 Kyiv

    private string _chatReply = "";
    private string? _backlog = "| AIS-1 | Existing row |";
    private readonly List<string> _chatBodies = [];
    private bool _planeDown;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (WeeklyIdeasJob Job, StubHandler Plane, StubHandler Telegram) Build(TimeProvider time, string backlogUrl = "http://backlog/file")
    {
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Telegram = new TelegramOptions { AllowedUserId = 42, Token = "T" },
            Plane = new PlaneOptions { BaseUrl = "http://plane/", Token = "t", Workspace = "w", FallbackProject = "FEED" },
            WeeklyIdeas = new WeeklyIdeasOptions { BacklogUrl = backlogUrl },
        });
        var plane = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/workspaces/w/projects/" => StubHandler.Json("""{"results":[{"id":"p-lab","identifier":"LAB"},{"id":"p-feed","identifier":"FEED"}]}"""),
            var p when p.EndsWith("/work-items/", StringComparison.Ordinal) => StubHandler.Json("""{"results":[{"name":"Existing plane item","state":"s"}]}"""),
            var p when p.EndsWith("/intake-issues/", StringComparison.Ordinal) && _planeDown => StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable),
            var p when p.EndsWith("/intake-issues/", StringComparison.Ordinal) => StubHandler.Json("""{"issue":"issue-1"}""", HttpStatusCode.Created),
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
            new WeeklyIdeaStore(pg.Db), new ProfileStore(pg.Db), new PlaneClient(plane.Client("http://plane/"), options),
            new BacklogClient(backlog.Client("http://backlog/"), options),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new TelegramClient(telegram.Client("http://tg/botT/")), new InboxStore(pg.Db), new QuietHours(cursors, options, time),
            cursors, options, new LoopHealth(time), time, NullLogger<WeeklyIdeasJob>.Instance);
        return (job, plane, telegram);
    }

    private IdeaDecisions Decisions(StubHandler plane, TimeProvider time)
    {
        var options = Options.Create(new FeedEaterOptions { Plane = new PlaneOptions { BaseUrl = "http://plane/", Token = "t", Workspace = "w", FallbackProject = "FEED" } });
        return new IdeaDecisions(new InboxStore(pg.Db), new WeeklyIdeaStore(pg.Db), new PlaneClient(plane.Client("http://plane/"), options), time);
    }

    private async Task<(IdeaDecisions Decisions, StubHandler Plane, long InboxId)> StagedAsync()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"Turn on caching","project":"LAB","where":"homelab-gitops","effort":"S","why":"Saves requests."}]}""";
        var time = new FakeTimeProvider(Monday);
        var (job, plane, _) = Build(time);
        await job.TickAsync(default);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<long>("select id from inbox where kind = 'idea'");
        return (Decisions(plane, time), plane, id);
    }

    private async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql, args);
    }

    private async Task<string> SeedAsync()
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync([new Profile { Key = "homelab", Kind = "project", PlaneIdentifier = "LAB", Description = "The box", Embedding = TestVectors.OneHot(0) }], DateTimeOffset.UtcNow, default);
        var improve = await Seed.ItemAsync(pg, 1, "Tool A adds caching", TestVectors.OneHot(1));
        await Seed.ReadAsync(pg, improve, "homelab", "improve");
        var liked = await Seed.ItemAsync(pg, 1, "Tool B showcase", TestVectors.OneHot(2));
        await new FeedbackStore(pg.Db).SetVoteAsync(liked, 1, default);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update reads set suggestion = 'Turn on caching in the box.' where item_id = @improve", new { improve });
        await c.ExecuteAsync("insert into signals (source, external_id, url, title, embedding, at) values ('karakeep', 'k1', 'https://example.org/bookmarked', 'A bookmarked tool', @e::real[]::vector, now())", new { e = TestVectors.OneHot(3) });
        return await c.ExecuteScalarAsync<string>("select url from items where id = @improve", new { improve }) ?? "";
    }

    [Fact]
    public async Task The_shortlist_stages_ideas_and_files_nothing_and_the_top_go_to_Telegram_with_buttons()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"Turn on caching","project":"LAB","where":"homelab-gitops","effort":"S","why":"Saves requests."}]}""";
        var (job, plane, telegram) = Build(new FakeTimeProvider(Monday));

        await job.TickAsync(default);

        Assert.DoesNotContain(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from inbox where kind = 'idea'"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from weekly_ideas"));
        Assert.Contains("homelab-gitops", await ScalarAsync<string>("select payload->>'html' from inbox where kind = 'idea'"), StringComparison.Ordinal);
        var message = Assert.Single(telegram.Calls);
        Assert.Contains("Ideas for 2026-W43", message.Body, StringComparison.Ordinal);
        Assert.Contains("Turn on caching", message.Body, StringComparison.Ordinal);
        Assert.Contains("ik:", message.Body, StringComparison.Ordinal);
        Assert.Contains("id:", message.Body, StringComparison.Ordinal);
        Assert.Contains("AIS-1", _chatBodies.Single(), StringComparison.Ordinal);            // the backlog reached the model
        Assert.Contains("Existing plane item", _chatBodies.Single(), StringComparison.Ordinal);
        Assert.Contains("A bookmarked tool", _chatBodies.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keep_files_once_and_a_second_keep_is_already_decided()
    {
        var (decisions, plane, inboxId) = await StagedAsync();

        var first = await decisions.KeepAsync(inboxId, default);
        var second = await decisions.KeepAsync(inboxId, default);

        Assert.Equal(IdeaOutcome.Filed, first.Outcome);
        Assert.Equal("LAB", first.Project);
        Assert.Equal(IdeaOutcome.AlreadyDecided, second.Outcome);
        Assert.Single(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("filed", await ScalarAsync<string>("select outcome from weekly_ideas"));
    }

    [Fact]
    public async Task Drop_remembers_the_source_and_files_nothing()
    {
        var (decisions, plane, inboxId) = await StagedAsync();

        Assert.Equal(IdeaOutcome.Dropped, (await decisions.DropAsync(inboxId, default)).Outcome);
        Assert.Equal(IdeaOutcome.AlreadyDecided, (await decisions.KeepAsync(inboxId, default)).Outcome);
        Assert.Equal(IdeaOutcome.NotFound, (await decisions.KeepAsync(inboxId + 1000, default)).Outcome);
        Assert.DoesNotContain(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("dropped", await ScalarAsync<string>("select outcome from weekly_ideas"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from weekly_ideas where plane_issue_id is null"));
    }

    [Fact]
    public async Task Keep_while_Plane_is_down_leaves_the_idea_open()
    {
        var (decisions, _, inboxId) = await StagedAsync();
        _planeDown = true;

        Assert.Equal(IdeaOutcome.Failed, (await decisions.KeepAsync(inboxId, default)).Outcome);
        Assert.Null(await ScalarAsync<DateTime?>("select done_at from inbox where id = @inboxId", new { inboxId }));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from weekly_ideas"));

        _planeDown = false;
        Assert.Equal(IdeaOutcome.Filed, (await decisions.KeepAsync(inboxId, default)).Outcome);
    }

    [Fact]
    public async Task A_source_filed_before_is_not_offered_again_and_a_run_with_no_leads_asks_nothing()
    {
        var url = await SeedAsync();
        await new WeeklyIdeaStore(pg.Db).AddAsync(new FiledIdea(FeedEater.Text.UrlCanonicalizer.Canonical(url), "2026-W42", "Earlier", "LAB", "i0"), default);
        _chatReply = """{"ideas":[]}""";
        var (job, plane, telegram) = Build(new FakeTimeProvider(Monday));

        await job.TickAsync(default);

        Assert.DoesNotContain("Tool A adds caching", _chatBodies.Single(), StringComparison.Ordinal);
        Assert.Contains("Tool B showcase", _chatBodies.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain(plane.Calls, c => c.Method == HttpMethod.Post);
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
        _chatReply = """{"ideas":[{"lead":1,"idea":"X","project":"LAB"}]}""";
        var (job, plane, _) = Build(new FakeTimeProvider(Monday));

        await Assert.ThrowsAsync<HttpRequestException>(() => job.TickAsync(default));

        Assert.DoesNotContain(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Empty(_chatBodies);
        Assert.Null(await new CursorStore(pg.Db).GetAsync("job:weekly-ideas", default));
    }

    [Fact]
    public async Task Runs_once_per_Monday()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"X","project":"LAB"}]}""";
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
        _chatReply = """{"ideas":[{"lead":1,"idea":"X","project":"LAB"}]}""";
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
