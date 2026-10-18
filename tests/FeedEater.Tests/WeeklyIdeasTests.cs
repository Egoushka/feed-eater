using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Llm;
using FeedEater.Loops;
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

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (WeeklyIdeasJob Job, StubHandler Plane, StubHandler Telegram) Build(TimeProvider time, string backlogUrl = "http://backlog/file")
    {
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Telegram = new TelegramOptions { AllowedUserId = 42 },
            Plane = new PlaneOptions { BaseUrl = "http://plane/", Token = "t", Workspace = "w", FallbackProject = "FEED" },
            WeeklyIdeas = new WeeklyIdeasOptions { BacklogUrl = backlogUrl },
        });
        var plane = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/workspaces/w/projects/" => StubHandler.Json("""{"results":[{"id":"p-lab","identifier":"LAB"},{"id":"p-feed","identifier":"FEED"}]}"""),
            var p when p.EndsWith("/work-items/", StringComparison.Ordinal) => StubHandler.Json("""{"results":[{"name":"Existing plane item","state":"s"}]}"""),
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
            new TelegramClient(telegram.Client("http://tg/botT/")), new QuietHours(cursors, options, time),
            cursors, options, new LoopHealth(time), time, NullLogger<WeeklyIdeasJob>.Instance);
        return (job, plane, telegram);
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
    public async Task Files_the_ranked_ideas_in_Plane_records_them_and_sends_the_top_to_Telegram()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"Turn on caching","project":"LAB","where":"homelab-gitops","effort":"S","why":"Saves requests."}]}""";
        var (job, plane, telegram) = Build(new FakeTimeProvider(Monday));

        await job.TickAsync(default);

        var post = Assert.Single(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("http://plane/api/v1/workspaces/w/projects/p-lab/intake-issues/", post.Uri);
        Assert.Contains("Turn on caching", post.Body, StringComparison.Ordinal);
        Assert.Contains("homelab-gitops", post.Body, StringComparison.Ordinal);
        Assert.Equal(["Turn on caching"], await new WeeklyIdeaStore(pg.Db).RecentTitlesAsync(10, default));
        var message = Assert.Single(telegram.Calls);
        Assert.Contains("Ideas for 2026-W43", message.Body, StringComparison.Ordinal);
        Assert.Contains("Turn on caching", message.Body, StringComparison.Ordinal);
        Assert.Contains("AIS-1", _chatBodies.Single(), StringComparison.Ordinal);            // the backlog reached the model
        Assert.Contains("Existing plane item", _chatBodies.Single(), StringComparison.Ordinal);
        Assert.Contains("A bookmarked tool", _chatBodies.Single(), StringComparison.Ordinal);
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
        var (job, plane, _) = Build(new FakeTimeProvider(Monday));

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Single(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("2026-10-19", await new CursorStore(pg.Db).GetAsync("job:weekly-ideas", default));
    }

    [Fact]
    public async Task A_force_request_runs_again_once_and_is_cleared()
    {
        await SeedAsync();
        _chatReply = """{"ideas":[{"lead":1,"idea":"X","project":"LAB"}]}""";
        var (job, plane, _) = Build(new FakeTimeProvider(Monday));
        var cursors = new CursorStore(pg.Db);

        await job.TickAsync(default);
        await cursors.SetAsync(WeeklyIdeasJob.ForceCursor, "1", default);
        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(2, plane.Calls.Count(c => c.Method == HttpMethod.Post));
        Assert.Null(await cursors.GetAsync(WeeklyIdeasJob.ForceCursor, default));
    }
}
