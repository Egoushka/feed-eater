using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Library;
using FeedEater.Issues;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedbackLoopTests(PostgresFixture pg) : IAsyncLifetime
{
    private System.Net.HttpStatusCode _karakeepStatus = System.Net.HttpStatusCode.Created;
    private string _karakeepToken = "";
    private bool _mirror;
    private bool _localIdeas;
    private readonly List<string> _karakeepBodies = [];
    private System.Net.HttpStatusCode _githubStatus = System.Net.HttpStatusCode.Created;
    private string _githubBody = """{"number":5}""";

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (ItemActions Actions, StubHandler GitHub) Build()
    {
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv",
            Karakeep = new KarakeepOptions { BaseUrl = "http://karakeep/", Token = _karakeepToken, Mirror = _mirror },
            GitHub = _localIdeas ? new GitHubOptions() : new GitHubOptions { Token = "p", FallbackRepo = "octo/feed" },
        });
        var karakeepStub = new StubHandler((_, body) =>
        {
            _karakeepBodies.Add(body);
            return StubHandler.Json("""{"id":"bm-1"}""", _karakeepStatus);
        });
        var githubStub = new StubHandler((request, _) => request.Method == HttpMethod.Post
            ? StubHandler.Json(_githubBody, _githubStatus)
            : StubHandler.Json("[]"));
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        IIdeaSink sink = _localIdeas ? new LocalIdeaSink() : new GitHubIdeaSink(new GitHubIssues(githubStub.Client("http://github/")), options);
        var filer = new IdeaFiler(items, feedback, new ProfileStore(pg.Db), sink, TimeProvider.System);
        var actions = new ItemActions(feedback, filer, Seed.Library(pg, options, karakeepStub), NullLogger<ItemActions>.Instance);
        return (actions, githubStub);
    }

    private async Task<long> SeedReadItemAsync(string? suggestion, string? project = "homelab")
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [new Profile { Key = "homelab", Kind = "project", Repo = "octo/homelab", Description = "VPS.", Embedding = TestVectors.OneHot(0) }],
            DateTimeOffset.UtcNow, default);
        var id = await Seed.ItemAsync(pg, 1, "Backup tool", TestVectors.OneHot(0));
        await new AnalysisStore(pg.Db).SaveReadAsync(id, new ReadResult
        {
            Summary = "S.",
            Why = "W.",
            Kind = suggestion is null ? "fyi" : "improve",
            Project = project,
            Suggestion = suggestion,
        }, "m", default);
        return id;
    }

    [Fact]
    public async Task A_vote_is_stored_and_clearing_it_removes_it()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (actions, _) = Build();

        await actions.VoteAsync(id, 1, default);
        Assert.Equal(1, (await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);

        await actions.ClearVoteAsync(id, default);
        Assert.Null((await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
    }

    [Fact]
    public async Task Two_idea_requests_file_one_issue_in_the_matched_repo()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (actions, github) = Build();

        var first = await actions.FileIdeaAsync(id, default);
        var second = await actions.FileIdeaAsync(id, default);

        var post = Assert.Single(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("http://github/repos/octo/homelab/issues", post.Uri);
        Assert.Contains("\"labels\":[\"triage\"]", post.Body, StringComparison.Ordinal);
        var item = (await new ItemStore(pg.Db).GetAsync(id, default))!;
        Assert.Equal("octo/homelab", item.FiledIn);
        Assert.Equal(1, item.Vote);
        Assert.Equal(5, (await new FeedbackStore(pg.Db).GetIdeaAsync(id, default))!.IssueNumber);
        Assert.Equal(("octo/homelab", null), first);
        Assert.Equal(("octo/homelab", null), second);
    }

    [Fact]
    public async Task With_the_local_sink_the_idea_is_saved_without_calling_GitHub()
    {
        _localIdeas = true;
        var id = await SeedReadItemAsync("Try it.");
        var (actions, github) = Build();

        var (project, error) = await actions.FileIdeaAsync(id, default);

        Assert.Null(error);
        Assert.Equal("homelab", project);
        Assert.Empty(github.Calls);
        var item = (await new ItemStore(pg.Db).GetAsync(id, default))!;
        Assert.Equal(("homelab", 1), (item.FiledIn, item.Vote));
        Assert.Null((await new FeedbackStore(pg.Db).GetIdeaAsync(id, default))!.IssueNumber);
    }

    [Fact]
    public async Task An_item_already_filed_earlier_files_nothing_new()
    {
        var id = await SeedReadItemAsync("Try it.");
        await new FeedbackStore(pg.Db).AddIdeaAsync(
            new Idea { ItemId = id, Project = "LAB", IssueNumber = 1, Title = "Try it.", At = DateTime.UtcNow.AddDays(-7) }, default);
        var (actions, github) = Build();

        var (project, _) = await actions.FileIdeaAsync(id, default);

        Assert.DoesNotContain(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("LAB", project);
    }

    [Fact]
    public async Task An_item_without_a_read_project_is_filed_under_its_ranking_profile_not_the_fallback()
    {
        var id = await SeedReadItemAsync("Try it.", project: null);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set profile_key = 'homelab' where id = @id", new { id });
        }

        var (actions, github) = Build();

        await actions.FileIdeaAsync(id, default);

        var post = Assert.Single(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("http://github/repos/octo/homelab/issues", post.Uri);
        Assert.Equal("octo/homelab", (await new ItemStore(pg.Db).GetAsync(id, default))!.FiledIn);
    }

    [Fact]
    public async Task An_item_without_any_matching_profile_is_filed_in_the_fallback_repo()
    {
        var id = await SeedReadItemAsync("Try it.", project: null);
        var (actions, github) = Build();

        await actions.FileIdeaAsync(id, default);

        var post = Assert.Single(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("http://github/repos/octo/feed/issues", post.Uri);
    }

    [Theory]
    [InlineData("""{"detail":"boom"}""", System.Net.HttpStatusCode.InternalServerError)]
    [InlineData("""not json""", System.Net.HttpStatusCode.Created)]
    [InlineData("""{"nope":1}""", System.Net.HttpStatusCode.Created)]
    public async Task A_GitHub_failure_is_returned_and_nothing_is_stored(string body, System.Net.HttpStatusCode status)
    {
        var id = await SeedReadItemAsync("Try it.");
        var (actions, _) = Build();
        _githubBody = body;
        _githubStatus = status;

        var (project, error) = await actions.FileIdeaAsync(id, default);

        Assert.Null(project);
        Assert.NotNull(error);
        var item = (await new ItemStore(pg.Db).GetAsync(id, default))!;
        Assert.Null(item.FiledIn);
        Assert.Null(item.Vote);
    }

    [Fact]
    public async Task An_item_without_a_suggestion_is_not_filed()
    {
        var id = await SeedReadItemAsync(null);
        var (actions, github) = Build();

        var (project, error) = await actions.FileIdeaAsync(id, default);

        Assert.Null(project);
        Assert.Contains("this item has no suggestion to file", error, StringComparison.Ordinal);
        Assert.DoesNotContain(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Null((await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
    }

    [Fact]
    public async Task Filing_with_his_own_text_in_another_project_uses_that_project_and_text()
    {
        var id = await SeedReadItemAsync("Try it.");
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [
                new Profile { Key = "homelab", Kind = "project", Repo = "octo/homelab", Description = "VPS.", Embedding = TestVectors.OneHot(0) },
                new Profile { Key = "jarvis", Kind = "project", Repo = "octo/jarvis", Description = "Assistant.", Embedding = TestVectors.OneHot(2) },
            ],
            DateTimeOffset.UtcNow, default);
        var (actions, github) = Build();

        var (project, error) = await actions.FileIdeaAsync(id, "jarvis", "Use it for JARVIS memory.", default);

        Assert.Null(error);
        Assert.Equal("octo/jarvis", project);
        var post = Assert.Single(github.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("http://github/repos/octo/jarvis/issues", post.Uri);
        Assert.Contains("Use it for JARVIS memory.", post.Body, StringComparison.Ordinal);
        Assert.Equal("octo/jarvis", (await new ItemStore(pg.Db).GetAsync(id, default))!.FiledIn);
    }

    [Fact]
    public async Task An_unknown_project_files_nothing()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (actions, github) = Build();

        var (project, error) = await actions.FileIdeaAsync(id, "Nytka", null, default);

        Assert.Null(project);
        Assert.NotNull(error);
        Assert.DoesNotContain(github.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Saving_goes_into_the_library_without_Karakeep_counts_as_a_thumbs_up_and_is_idempotent()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (actions, _) = Build();

        var first = await actions.SaveAsync(id, "ui", default);
        var second = await actions.SaveAsync(id, "ui", default);

        Assert.Equal((SaveOutcome.Saved, SaveOutcome.AlreadySaved), (first, second));
        Assert.Empty(_karakeepBodies);
        Assert.True((await new ItemStore(pg.Db).GetAsync(id, default))!.Saved);
        Assert.Equal("ui", await SourceOfSavedAsync(id));
        Assert.Equal(((short)1, 1f), await new FeedbackStore(pg.Db).VoteOfAsync(id, default));
    }

    [Fact]
    public async Task A_save_with_Karakeep_mirroring_down_still_saves()
    {
        var id = await SeedReadItemAsync("Try it.");
        (_karakeepToken, _mirror, _karakeepStatus) = ("k", true, System.Net.HttpStatusCode.ServiceUnavailable);
        var (actions, _) = Build();

        Assert.Equal(SaveOutcome.Saved, await actions.SaveAsync(id, "ui", default));
        Assert.True((await new ItemStore(pg.Db).GetAsync(id, default))!.Saved);
    }

    [Fact]
    public async Task Saving_an_unknown_item_says_so()
    {
        var (actions, _) = Build();

        Assert.Equal(SaveOutcome.NoItem, await actions.SaveAsync(99999, "ui", default));
    }

    private async Task<string> SourceOfSavedAsync(long id)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await Dapper.SqlMapper.ExecuteScalarAsync<string>(c, "select source from saved where item_id = @id", new { id }) ?? "";
    }
}
