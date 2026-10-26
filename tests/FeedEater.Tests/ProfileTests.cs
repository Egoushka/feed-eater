using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Issues;
using FeedEater.Profiles;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProfileTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Parses_the_profile_file()
    {
        var file = ProfileFile.Load("Fixtures/profile.json");

        Assert.Equal(3, file.Projects.Count);
        Assert.Equal(2, file.Topics.Count);
        Assert.Equal("octo/homelab", file.Projects[1].Repo);
        Assert.Null(file.Projects[2].Repo);
        Assert.Equal("Agent orchestrator.", ProfileFile.OneLiner(file.Projects[0].Description));
    }

    [Fact]
    public void A_missing_profile_file_means_the_built_in_example_and_an_invalid_one_still_fails()
    {
        var (example, isExample) = ProfileFile.LoadOrExample("/nope/profile.json");
        var (file, fromFile) = ProfileFile.LoadOrExample("Fixtures/profile.json");

        Assert.True(isExample);
        Assert.NotEmpty(example.About);
        Assert.NotEmpty(example.Projects.Concat(example.Topics));
        Assert.All(example.Projects, p => Assert.Null(p.Repo));   // an example must never file ideas in a real repo
        Assert.False(fromFile);
        Assert.Equal(3, file.Projects.Count);
        var broken = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(broken, "{}");
        Assert.ThrowsAny<Exception>(() => ProfileFile.LoadOrExample(broken));
        Assert.Equal("Using the example interests; edit /config/profile.json", ProfileFile.ExampleNote("/config/profile.json"));
    }

    [Fact]
    public void A_directory_at_the_profile_path_is_an_error_not_the_example()
    {
        var directory = Directory.CreateTempSubdirectory("profile-dir-").FullName;
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => ProfileFile.LoadOrExample(directory));

            Assert.Contains("is a directory, not a file", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task The_builder_builds_from_the_example_when_the_profile_file_is_missing()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var options = Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv", ProfilePath = "/nope/profile.json" });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
        var profiles = new ProfileStore(pg.Db);
        var builder = new ProfileBuilder(
            new GitHubIssues(new HttpClient()), profiles,
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<ProfileBuilder>.Instance);

        await builder.TickAsync(default);

        Assert.NotEmpty(await profiles.AllAsync(default));
    }

    [Fact]
    public void Rejects_duplicate_keys_missing_sections_and_empty_descriptions()
    {
        Assert.Throws<InvalidDataException>(() => ProfileFile.Parse(
            """{"about":"a","projects":[{"key":"x","description":"d"}],"topics":[{"key":"x","description":"d"}]}"""));
        Assert.Throws<JsonException>(() => ProfileFile.Parse("""{"about":"a","projects":[]}"""));
        Assert.Throws<InvalidDataException>(() => ProfileFile.Parse(
            """{"about":"a","projects":[{"key":"x","description":" "}],"topics":[]}"""));
        Assert.Throws<InvalidDataException>(() => ProfileFile.Parse("""{"about":"a","projects":[],"topics":[]}"""));
        Assert.Throws<InvalidDataException>(() => ProfileFile.Parse(
            """{"about":"a","projects":[{"key":"x","description":"d","repo":"LAB"}],"topics":[]}"""));   // a Plane identifier is not a repo
    }

    [Fact]
    public async Task Builder_embeds_descriptions_with_open_issues_and_survives_a_github_error()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var github = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/repos/octo/homelab/issues" => StubHandler.Json(request.RequestUri.Query.Contains("state=open", StringComparison.Ordinal)
                ? """[{"title":"Rotate backups"},{"title":"Bump it","pull_request":{}}]"""
                : """[{"title":"Old thing"}]"""),
            _ => StubHandler.Json("{}", System.Net.HttpStatusCode.InternalServerError),
        });
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var options = Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv", ProfilePath = "Fixtures/profile.json", GitHub = new GitHubOptions { Token = "t", FallbackRepo = "octo/feed" } });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero)); // 04:00 Kyiv
        var profiles = new ProfileStore(pg.Db);
        var builder = new ProfileBuilder(
            new GitHubIssues(github.Client("http://github/")), profiles,
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<ProfileBuilder>.Instance);

        await builder.TickAsync(default);

        var stored = await profiles.AllAsync(default);
        Assert.Equal(5, stored.Count);
        Assert.Equal(3, stored.Count(p => p.Kind == "project"));
        Assert.Equal(TestVectors.Dims, stored[0].Embedding.Length);
        var input = llm.Calls.Single().Body;
        Assert.Contains("Open work: Rotate backups", input, StringComparison.Ordinal);
        Assert.DoesNotContain("Old thing", input, StringComparison.Ordinal);
        Assert.DoesNotContain("Bump it", input, StringComparison.Ordinal);
        Assert.Equal("octo/homelab", stored.Single(p => p.Key == "homelab").Repo);
        Assert.Contains("Agent orchestrator. Runs read-only coding agents and returns cited claims.\"", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builder_falls_back_to_plain_descriptions_when_github_answers_with_an_unexpected_shape()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var github = new StubHandler((_, _) => StubHandler.Json("{}"));
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var options = Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv", ProfilePath = "Fixtures/profile.json", GitHub = new GitHubOptions { Token = "t", FallbackRepo = "octo/feed" } });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
        var profiles = new ProfileStore(pg.Db);
        var builder = new ProfileBuilder(
            new GitHubIssues(github.Client("http://github/")), profiles,
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<ProfileBuilder>.Instance);

        await builder.TickAsync(default);

        Assert.Equal(5, (await profiles.AllAsync(default)).Count);
        Assert.DoesNotContain("Open work", llm.Calls.Single().Body, StringComparison.Ordinal);
    }
}
