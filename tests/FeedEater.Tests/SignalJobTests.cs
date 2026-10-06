using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Signals;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SignalJobTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static StubHandler Karakeep() => new((request, _) => StubHandler.Json(request.RequestUri!.Query.Contains("cursor=c2", StringComparison.Ordinal)
        ? """{"bookmarks":[{"id":"b3","createdAt":"2026-10-01T10:00:00Z","title":null,"content":{"type":"link","url":"https://c.example","title":"Third","description":"desc"}}],"nextCursor":null}"""
        : """
          {"bookmarks":[
            {"id":"b1","createdAt":"2026-10-04T10:00:00Z","title":"First","content":{"type":"link","url":"https://a.example","title":"First","description":null}},
            {"id":"b2","createdAt":"2026-10-03T10:00:00Z","title":"A note","content":{"type":"text","text":"just text"}}
          ],"nextCursor":"c2"}
          """));

    private static StubHandler GitHub() => new((request, _) => StubHandler.Json(request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal)
        ? """[{"starred_at":"2026-10-02T08:00:00Z","repo":{"full_name":"pgvector/pgvector","html_url":"https://github.com/pgvector/pgvector","description":"Vector search for Postgres","topics":["postgres","vectors"]}}]"""
        : "[]"));

    private SignalJob Build(StubHandler karakeep, StubHandler github, StubHandler llm, string token = "k", int embedBatch = 64, string githubUser = "octocat")
    {
        var options = Options.Create(new FeedEaterOptions
        {
            Karakeep = new KarakeepOptions { BaseUrl = "http://karakeep/", Token = token },
            GitHub = new GitHubOptions { User = githubUser },
            Llm = new LlmOptions { EmbedBatch = embedBatch },
        });
        return new SignalJob(
            new KarakeepClient(karakeep.Client("http://karakeep/")), new GitHubStarsClient(github.Client("http://github/"), options),
            new SignalStore(pg.Db), new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new CursorStore(pg.Db), options, new LoopHealth(TimeProvider.System), TimeProvider.System, NullLogger<SignalJob>.Instance);
    }

    [Fact]
    public async Task Collects_link_bookmarks_and_stars_then_stops_at_known_ones()
    {
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var karakeep = Karakeep();
        var job = Build(karakeep, GitHub(), llm);

        Assert.Equal(3, await job.CollectAsync(default));
        var callsAfterFirst = karakeep.Calls.Count;
        Assert.Equal(0, await job.CollectAsync(default));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var ids = (await c.QueryAsync<string>("select external_id from signals order by external_id")).ToList();
        Assert.Equal(["b1", "b3", "pgvector/pgvector"], ids);
        Assert.Equal(callsAfterFirst + 1, karakeep.Calls.Count);   // second run stops on the first page at b1
        Assert.Contains("pgvector/pgvector: Vector search for Postgres postgres vectors", llm.Calls[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_fails_midway_keeps_a_prefix_and_the_retry_stores_the_rest()
    {
        var failing = true;
        var llm = new StubHandler((_, body) => failing && body.Contains("Middle", StringComparison.Ordinal)
            ? StubHandler.Json("{}", System.Net.HttpStatusCode.InternalServerError)
            : StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var karakeep = new StubHandler((_, _) => StubHandler.Json("""
            {"bookmarks":[
              {"id":"n1","createdAt":"2026-10-04T10:00:00Z","title":"Newest","content":{"type":"link","url":"https://1.example"}},
              {"id":"n2","createdAt":"2026-10-03T10:00:00Z","title":"Middle","content":{"type":"link","url":"https://2.example"}},
              {"id":"n3","createdAt":"2026-10-02T10:00:00Z","title":"Oldest","content":{"type":"link","url":"https://3.example"}}
            ],"nextCursor":null}
            """));
        var job = Build(karakeep, GitHub(), llm, embedBatch: 1);

        await Assert.ThrowsAsync<HttpRequestException>(() => job.CollectAsync(default));
        failing = false;
        Assert.Equal(3, await job.CollectAsync(default));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var ids = (await c.QueryAsync<string>("select external_id from signals order by external_id")).ToList();
        Assert.Equal(["n1", "n2", "n3", "pgvector/pgvector"], ids);
    }

    [Fact]
    public async Task Without_a_Karakeep_token_only_stars_are_collected()
    {
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var karakeep = Karakeep();

        Assert.Equal(1, await Build(karakeep, GitHub(), llm, token: "").CollectAsync(default));
        Assert.Empty(karakeep.Calls);
    }

    [Fact]
    public async Task A_bookmark_without_content_has_no_url_and_is_skipped()
    {
        var karakeep = new StubHandler((_, _) => StubHandler.Json("""{"bookmarks":[{"id":"x","createdAt":"2026-10-04T10:00:00Z","title":"t"}],"nextCursor":null}"""));
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));

        Assert.Equal(1, await Build(karakeep, GitHub(), llm).CollectAsync(default));   // only the star
    }

    [Fact]
    public async Task A_failing_source_does_not_stop_the_others()
    {
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var github = new StubHandler((_, _) => StubHandler.Json("{}", System.Net.HttpStatusCode.Forbidden));

        Assert.Equal(2, await Build(Karakeep(), github, llm).CollectAsync(default));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(["b1", "b3"], (await c.QueryAsync<string>("select external_id from signals order by external_id")).ToList());
    }

    [Fact]
    public async Task When_every_source_is_down_the_run_ends_quietly_and_the_next_daily_run_catches_up()
    {
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var down = new StubHandler((_, _) => StubHandler.Json("{}", System.Net.HttpStatusCode.BadGateway));

        Assert.Equal(0, await Build(down, down, llm).CollectAsync(default));
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task Without_a_GitHub_user_there_is_no_stars_import()
    {
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var github = GitHub();

        Assert.Equal(2, await Build(Karakeep(), github, llm, githubUser: "").CollectAsync(default));
        Assert.Empty(github.Calls);
    }

    [Fact]
    public async Task With_neither_source_configured_nothing_is_called()
    {
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var (karakeep, github) = (Karakeep(), GitHub());

        Assert.Equal(0, await Build(karakeep, github, llm, token: "", githubUser: "").CollectAsync(default));
        Assert.Empty(karakeep.Calls);
        Assert.Empty(github.Calls);
        Assert.Empty(llm.Calls);
    }
}
