using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedbackLoopTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<(string Method, string Body)> _telegram = [];
    private string _updates = """{"ok":true,"result":[]}""";
    private bool _editNotModified;
    private bool _editFails;
    private bool _answerFails;
    private System.Net.HttpStatusCode _planeStatus = System.Net.HttpStatusCode.Created;
    private string _planeBody = """{"issue":{"id":"issue-1"}}""";

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Callback(long updateId, long from, string data) =>
        $$$"""{"update_id":{{{updateId}}},"callback_query":{"id":"cb{{{updateId}}}","from":{"id":{{{from}}}},"message":{"message_id":7,"chat":{"id":42}},"data":"{{{data}}}"}}""";

    private (TelegramPoller Poller, StubHandler Plane) Build()
    {
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var telegramStub = new StubHandler((request, body) =>
        {
            var method = request.RequestUri!.Segments[^1];
            _telegram.Add((method, body));
            return method switch
            {
                "getUpdates" => StubHandler.Json(Interlocked.Exchange(ref _updates, """{"ok":true,"result":[]}""")),
                "editMessageReplyMarkup" when _editNotModified => StubHandler.Json("""{"ok":false,"description":"Bad Request: message is not modified"}"""),
                "editMessageReplyMarkup" when _editFails => StubHandler.Json("""{"ok":false,"description":"Bad Request: message to edit not found"}"""),
                "answerCallbackQuery" when _answerFails => StubHandler.Json("""{"ok":false,"description":"Bad Request: query is too old"}"""),
                _ => StubHandler.Json("""{"ok":true,"result":true}"""),
            };
        });
        var planeStub = new StubHandler((request, _) => request.Method == HttpMethod.Post
            ? StubHandler.Json(_planeBody, _planeStatus)
            : StubHandler.Json("""{"results":[{"id":"p-lab","identifier":"LAB"},{"id":"p-feed","identifier":"FEED"}]}"""));
        var telegram = new TelegramClient(telegramStub.Client("http://tg/botT/"));
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var filer = new IdeaFiler(items, feedback, new ProfileStore(pg.Db), new PlaneClient(planeStub.Client("http://plane/"), options), options, TimeProvider.System);
        var handler = new CallbackHandler(telegram, feedback, filer, items, options, NullLogger<CallbackHandler>.Instance);
        var commands = new CommandHandler(telegram, new DigestTrigger(new CursorStore(pg.Db), options, TimeProvider.System), options);
        var poller = new TelegramPoller(telegram, handler, commands, new CursorStore(pg.Db), new LoopHealth(TimeProvider.System), TimeProvider.System, NullLogger<TelegramPoller>.Instance);
        return (poller, planeStub);
    }

    private async Task<long> SeedReadItemAsync(string? suggestion, string? project = "homelab")
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [new Profile { Key = "homelab", Kind = "project", PlaneIdentifier = "LAB", Description = "VPS.", Embedding = TestVectors.OneHot(0) }],
            DateTimeOffset.UtcNow, default);
        var id = await Seed.ItemAsync(pg, 1, "Backup tool", TestVectors.OneHot(0));
        await new AnalysisStore(pg.Db).SaveReadAsync(id, new ReadResult
        {
            Summary = "S.", Why = "W.", Kind = suggestion is null ? "fyi" : "improve", Project = project, Suggestion = suggestion,
        }, "m", default);
        return id;
    }

    private string Answers() => string.Join('\n', _telegram.Where(t => t.Method == "answerCallbackQuery").Select(t => t.Body));

    [Fact]
    public async Task A_vote_is_stored_marked_on_the_buttons_and_the_offset_advances()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, _) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"v:{id}:u")}}]}""";

        await poller.TickAsync(default);

        Assert.Equal(1, (await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
        var markup = JsonSerializer.Deserialize<JsonElement>(_telegram.Single(t => t.Method == "editMessageReplyMarkup").Body);
        Assert.Equal("👍 ✓", markup.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("text").GetString());
        Assert.Equal("11", await new CursorStore(pg.Db).GetAsync("telegram:offset", default));
    }

    [Fact]
    public async Task Someone_else_pressing_a_button_changes_nothing()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, _) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 999, $"v:{id}:u")}}]}""";

        await poller.TickAsync(default);

        Assert.Null((await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
        Assert.Contains("Not for you.", Answers(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_taps_on_the_idea_button_file_one_plane_item_in_the_matched_project()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, plane) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}},{{Callback(11, 42, $"i:{id}")}}]}""";

        await poller.TickAsync(default);

        var post = Assert.Single(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("/projects/p-lab/intake-issues/", post.Uri, StringComparison.Ordinal);
        var item = (await new ItemStore(pg.Db).GetAsync(id, default))!;
        Assert.Equal("LAB", item.FiledIn);
        Assert.Equal(1, item.Vote);
        Assert.Contains("Filed in LAB", Answers(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tap_on_an_item_already_filed_earlier_files_nothing_new()
    {
        var id = await SeedReadItemAsync("Try it.");
        await new FeedbackStore(pg.Db).AddIdeaAsync(
            new Idea { ItemId = id, PlaneProject = "LAB", PlaneIssueId = "old", Title = "Try it.", At = DateTime.UtcNow.AddDays(-7) }, default);
        var (poller, plane) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}}]}""";

        await poller.TickAsync(default);

        Assert.DoesNotContain(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("Filed in LAB", Answers(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_without_a_read_project_is_filed_under_its_ranking_profile_not_FEED()
    {
        var id = await SeedReadItemAsync("Try it.", project: null);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set profile_key = 'homelab' where id = @id", new { id });
        }

        var (poller, plane) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}}]}""";

        await poller.TickAsync(default);

        var post = Assert.Single(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("/projects/p-lab/intake-issues/", post.Uri, StringComparison.Ordinal);
        Assert.Equal("LAB", (await new ItemStore(pg.Db).GetAsync(id, default))!.FiledIn);
    }

    [Fact]
    public async Task An_item_without_any_matching_profile_is_filed_in_FEED()
    {
        var id = await SeedReadItemAsync("Try it.", project: null);
        var (poller, plane) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}}]}""";

        await poller.TickAsync(default);

        var post = Assert.Single(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("/projects/p-feed/intake-issues/", post.Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"detail":"boom"}""", System.Net.HttpStatusCode.InternalServerError)]
    [InlineData("""not json""", System.Net.HttpStatusCode.Created)]
    [InlineData("""{"nope":1}""", System.Net.HttpStatusCode.Created)]
    public async Task A_Plane_failure_is_answered_and_nothing_is_stored(string body, System.Net.HttpStatusCode status)
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, _) = Build();
        _planeBody = body;
        _planeStatus = status;
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}}]}""";

        await poller.TickAsync(default);

        Assert.Contains("Not filed", Answers(), StringComparison.Ordinal);
        var item = (await new ItemStore(pg.Db).GetAsync(id, default))!;
        Assert.Null(item.FiledIn);
        Assert.Null(item.Vote);
        Assert.Equal("11", await new CursorStore(pg.Db).GetAsync("telegram:offset", default));
    }

    [Fact]
    public async Task An_item_without_a_suggestion_is_not_filed_and_an_unchanged_keyboard_is_fine()
    {
        var id = await SeedReadItemAsync(null);
        var (poller, plane) = Build();
        _editNotModified = true;
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}},{{Callback(11, 42, $"v:{id}:d")}}]}""";

        await poller.TickAsync(default);

        Assert.DoesNotContain(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("Not filed: this item has no suggestion to file", Answers(), StringComparison.Ordinal);
        Assert.Equal(-1, (await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
    }

    [Fact]
    public async Task A_handler_that_throws_still_answers_the_button_and_the_offset_advances()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, _) = Build();
        _editFails = true;
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"v:{id}:u")}}]}""";

        await poller.TickAsync(default);

        Assert.Contains("callback_query_id", Answers(), StringComparison.Ordinal);
        Assert.Contains("Something went wrong", Answers(), StringComparison.Ordinal);
        Assert.Equal("11", await new CursorStore(pg.Db).GetAsync("telegram:offset", default));
    }

    [Fact]
    public async Task A_failing_fallback_answer_is_swallowed_and_the_next_update_still_runs()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, _) = Build();
        _editFails = true;
        _answerFails = true;
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"v:{id}:u")}},{{Callback(11, 42, $"v:{id}:d")}}]}""";

        await poller.TickAsync(default);

        Assert.Equal(-1, (await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
        Assert.Equal("12", await new CursorStore(pg.Db).GetAsync("telegram:offset", default));
    }

    private static string Message(long updateId, long from, string text) =>
        $$$"""{"update_id":{{{updateId}}},"message":{"message_id":8,"from":{"id":{{{from}}}},"chat":{"id":{{{from}}}},"text":"{{{text}}}"}}""";

    [Theory]
    [InlineData("/digest", "run")]
    [InlineData("/digest@feed_bot", "run")]
    [InlineData("/digest resend", "resend")]
    public async Task The_digest_command_from_the_owner_queues_a_forced_run_and_replies(string text, string mode)
    {
        var (poller, _) = Build();
        _updates = $$"""{"ok":true,"result":[{{Message(10, 42, text)}}]}""";

        await poller.TickAsync(default);

        var today = new DigestTrigger(new CursorStore(pg.Db), Options.Create(new FeedEaterOptions()), TimeProvider.System).Today();
        Assert.Equal($"{mode}:{today}", await new CursorStore(pg.Db).GetAsync("digest:force", default));
        Assert.Contains("Queued", Assert.Single(_telegram, t => t.Method == "sendMessage").Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(999, "/digest")]
    [InlineData(42, "hello")]
    public async Task Other_senders_and_other_text_queue_nothing(long from, string text)
    {
        var (poller, _) = Build();
        _updates = $$"""{"ok":true,"result":[{{Message(10, from, text)}}]}""";

        await poller.TickAsync(default);

        Assert.Null(await new CursorStore(pg.Db).GetAsync("digest:force", default));
        Assert.DoesNotContain(_telegram, t => t.Method == "sendMessage");
    }
}
