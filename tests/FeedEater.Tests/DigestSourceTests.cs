using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Signals;
using FeedEater.Sources;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

/// <summary>The digest with the built-in reader: full text through the safe fetcher and the failing-feeds note.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DigestSourceTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Today = "2026-10-05";
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 4, 30, 0, TimeSpan.Zero);
    private static readonly string Article = string.Join(' ', Enumerable.Repeat("The real article explains the Postgres change.", 60));

    private readonly List<string> _sent = [];
    private readonly List<string> _readPrompts = [];
    private HttpStatusCode _articleStatus = HttpStatusCode.OK;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [new Profile { Key = "homelab", Kind = "project", PlaneIdentifier = "LAB", Description = "VPS.", Embedding = TestVectors.OneHot(0) }], Now, default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private (DigestRun Run, StubHandler Miniflux, SourceKit Kit) Build(Func<HttpRequestMessage, HttpResponseMessage>? network = null)
    {
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json", Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var kit = new SourceKit(pg, network ?? (_ => _articleStatus == HttpStatusCode.OK
            ? SourceKit.Xml($"<html><head><title>Real article</title></head><body><article>{Article}</article></body></html>", "text/html")
            : SourceKit.Status(_articleStatus)));
        var miniflux = new StubHandler((_, _) => StubHandler.Json("""{"content":"<p>from miniflux</p>"}"""));
        var llm = new StubHandler((_, body) =>
        {
            if (body.Contains("\"model\":\"gpt-4.1-nano\"", StringComparison.Ordinal))
            {
                return StubHandler.Json(Chat("""{"relevance":3,"project":"homelab","kind":"improve","reason":"r"}"""));
            }

            _readPrompts.Add(body);
            return StubHandler.Json(Chat("""{"summary":"S.","why":"W.","kind":"improve","project":"homelab","suggestion":"Do it."}"""));
        });
        var telegram = new StubHandler((request, body) =>
        {
            if (request.RequestUri!.Segments[^1] == "sendMessage")
            {
                _sent.Add(JsonSerializer.Deserialize<JsonElement>(body).GetProperty("text").GetString()!);
            }

            return StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""");
        });
        var time = new FakeTimeProvider(Now);
        var run = new DigestRun(
            new ItemStore(pg.Db), new ReleaseStore(pg.Db), new ProfileStore(pg.Db), new FeedbackStore(pg.Db), new AnalysisStore(pg.Db), new DigestStore(pg.Db), new UsageStore(pg.Db),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new MinifluxClient(miniflux.Client("http://miniflux/")),
            new GitHubStarsClient(new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.NotFound)).Client("http://github/"), options),
            new TelegramClient(telegram.Client("http://tg/botT/")),
            new LoopHealth(time), new FeedEater.Ranking.TasteSwitch(new CursorStore(pg.Db), new FeedbackStore(pg.Db), options), options, time, NullLogger<DigestRun>.Instance,
            kit.Poller, new ArticleText(kit.Items, kit.Fetcher, options, NullLogger<ArticleText>.Instance));
        return (run, miniflux, kit);
    }

    private static string Chat(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content } } },
        usage = new { prompt_tokens = 100, completion_tokens = 20 },
    });

    private async Task<long> SeedItemAsync(string content)
    {
        var hourAgo = Now.UtcDateTime.AddHours(-1);
        return await Seed.ItemAsync(pg, 1, "Keep this one", TestVectors.OneHot(0), hourAgo, hourAgo, content);
    }

    private async Task<string> ContentAsync(long id)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.ExecuteScalarAsync<string>("select content from items where id = @id", new { id }))!;
    }

    [Fact]
    public async Task A_short_item_is_read_with_the_article_text_fetched_through_the_safe_fetcher()
    {
        var id = await SeedItemAsync("A short teaser.");
        var (run, miniflux, kit) = Build();

        await run.RunAsync(Today, default);

        var prompt = Assert.Single(_readPrompts);
        Assert.Contains("The real article explains the Postgres change.", prompt, StringComparison.Ordinal);
        Assert.Contains("Real article", prompt, StringComparison.Ordinal);   // the page title leads the extracted text
        Assert.StartsWith("Real article", await ContentAsync(id), StringComparison.Ordinal);   // stored, like the Miniflux path stores its fetch
        Assert.Empty(miniflux.Calls);
        Assert.Single(kit.Stub.Calls);
        Assert.Equal(2, _sent.Count);   // header and the item
    }

    [Fact]
    public async Task An_item_with_enough_text_of_its_own_is_not_fetched()
    {
        var own = string.Join(' ', Enumerable.Repeat("Already long enough on its own.", 80));
        var id = await SeedItemAsync(own);
        var (run, _, kit) = Build();

        await run.RunAsync(Today, default);

        Assert.Empty(kit.Stub.Calls);
        Assert.Equal(own, await ContentAsync(id));
        Assert.Contains("Already long enough on its own.", Assert.Single(_readPrompts), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_page_that_cannot_be_fetched_leaves_the_feed_text_and_the_digest_still_goes_out(HttpStatusCode status)
    {
        _articleStatus = status;
        var id = await SeedItemAsync("A short teaser.");
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Contains("A short teaser.", Assert.Single(_readPrompts), StringComparison.Ordinal);
        Assert.Equal("A short teaser.", await ContentAsync(id));
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task A_page_shorter_than_the_feed_text_does_not_replace_it()
    {
        var id = await SeedItemAsync("A teaser that is longer than the tiny page behind it.");
        var (run, _, _) = Build(_ => SourceKit.Xml("<html><body><p>tiny</p></body></html>", "text/html"));

        await run.RunAsync(Today, default);

        Assert.Equal("A teaser that is longer than the tiny page behind it.", await ContentAsync(id));
    }

    [Fact]
    public async Task The_header_names_feeds_that_keep_failing_and_encodes_the_names()
    {
        await SeedItemAsync(Article);
        var (run, _, kit) = Build(r => r.RequestUri!.Host == "bad.example" ? SourceKit.Status(HttpStatusCode.NotFound) : SourceKit.Xml("<html></html>", "text/html"));
        await kit.AddFeedAsync("https://bad.example/feed", "Broken <b>feed</b> & co");
        for (var i = 0; i < 3; i++)
        {
            kit.Time.Advance(TimeSpan.FromDays(2));
            await kit.Poller.RunAsync(default);
        }

        await run.RunAsync(Today, default);

        Assert.Contains("⚠️ Feed failing: Broken &lt;b&gt;feed&lt;/b&gt; &amp; co; some items may be missing", _sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Miniflux", _sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_healthy_set_of_feeds_adds_no_note()
    {
        await SeedItemAsync(Article);
        var (run, _, _) = Build();

        await run.RunAsync(Today, default);

        Assert.DoesNotContain("⚠️", _sent[0], StringComparison.Ordinal);
    }
}
