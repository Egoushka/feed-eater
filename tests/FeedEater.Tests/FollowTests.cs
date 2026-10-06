using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Digest;
using FeedEater.Duels;
using FeedEater.Follow;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FollowTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Hostile = "<script>alert(\"x\")</script> & <b>";

    // 12:00 in Kyiv (EEST).
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
    private readonly List<(string Method, string Body)> _telegram = [];
    private readonly List<string> _chatBodies = [];
    private string _summary = "The story grew.";
    private bool _modelDown;
    private bool _sendFails;
    private long _messageId = 99;

    public Task InitializeAsync()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        return pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Rig(StoryFollower Follower, FollowJob Job, CallbackHandler Callbacks, CommandHandler Commands, FollowStore Store);

    private Rig Build(Action<FeedEaterOptions>? tweak = null)
    {
        var settings = new FeedEaterOptions { TimeZone = "Europe/Kyiv", Telegram = new TelegramOptions { AllowedUserId = 42 } };
        tweak?.Invoke(settings);
        var options = Options.Create(settings);
        var telegram = new TelegramClient(new StubHandler((request, body) =>
        {
            var method = request.RequestUri!.Segments[^1];
            _telegram.Add((method, body));
            return method switch
            {
                "sendMessage" when _sendFails => StubHandler.Json("""{"ok":false,"description":"Bad Request: chat not found"}"""),
                "sendMessage" => StubHandler.Json($$$"""{"ok":true,"result":{"message_id":{{{++_messageId}}}}}"""),
                _ => StubHandler.Json("""{"ok":true,"result":true}"""),
            };
        }).Client("http://tg/botT/"));
        var llm = new LiteLlmClient(new StubHandler((_, body) =>
        {
            _chatBodies.Add(body);
            return _modelDown
                ? StubHandler.Json("{}", System.Net.HttpStatusCode.ServiceUnavailable)
                : StubHandler.Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = _summary } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } }));
        }).Client("http://llm/"), new UsageStore(pg.Db), options);
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var store = new FollowStore(pg.Db);
        var follower = new StoryFollower(store, items, telegram, llm, options, _time, NullLogger<StoryFollower>.Instance);
        IIdeaSink sink = new PlaneIdeaSink(new PlaneClient(new StubHandler((_, _) => StubHandler.Json("{}")).Client("http://plane/"), options), options);
        var filer = new IdeaFiler(items, feedback, new ProfileStore(pg.Db), sink, TimeProvider.System);
        var callbacks = new CallbackHandler(telegram, feedback, filer, items, new DuelStore(pg.Db), new FeedEater.Signals.KarakeepClient(new StubHandler((_, _) => StubHandler.Json("{}")).Client("http://k/")),
            options, NullLogger<CallbackHandler>.Instance, follower);
        var search = new FeedEater.Search.ArchiveSearch(items, llm);
        var replies = new ReplyHandler(telegram, callbacks, items, new ProfileStore(pg.Db), llm, sink, options, NullLogger<ReplyHandler>.Instance);
        var quiet = new QuietHours(new CursorStore(pg.Db), options, _time);
        var commands = new CommandHandler(
            telegram, new DigestTrigger(new CursorStore(pg.Db), options, _time), quiet, search, new FeedEater.Search.ArchiveAnswer(search, items, llm, options), replies,
            new FeedEater.Ranking.TasteSwitch(new CursorStore(pg.Db), feedback, options), options, null, follower);
        var job = new FollowJob(store, follower, quiet, options, new LoopHealth(_time), _time, NullLogger<FollowJob>.Instance);
        return new Rig(follower, job, callbacks, commands, store);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private Task<long> RootAsync(string title = "Root story", float[]? embedding = null) =>
        Seed.ItemAsync(pg, 1, title, embedding ?? TestVectors.OneHot(0), Now.AddHours(-2), Now.AddHours(-1));

    /// <summary>An item ingested five minutes after the follow starts, as the clusterer left it: considered, a head or a member.</summary>
    private async Task<long> LaterAsync(string title, float[] embedding, long? head, long feed = 2, int minutes = 5, bool considered = true)
    {
        var id = await Seed.ItemAsync(pg, feed, title, embedding, Now, Now.AddMinutes(minutes));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("update items set clustered = @considered, cluster_of = @head where id = @id", new { id, head, considered });
        return id;
    }

    private Task PressAsync(Rig rig, string data, long from = 42) => rig.Callbacks.HandleAsync(new TgCallback("cb", from, 42, 7, data), default);

    private List<JsonElement> Sends() => _telegram.Where(t => t.Method == "sendMessage").Select(t => JsonSerializer.Deserialize<JsonElement>(t.Body)).ToList();

    private List<string?> Answers() => _telegram.Where(t => t.Method == "answerCallbackQuery").Select(t => JsonSerializer.Deserialize<JsonElement>(t.Body))
        .Select(b => b.TryGetProperty("text", out var t) ? t.GetString() : null).ToList();

    private static string Text(JsonElement message) => message.GetProperty("text").GetString()!;

    private static long? ReplyTo(JsonElement message) => message.TryGetProperty("reply_parameters", out var r) ? r.GetProperty("message_id").GetInt64() : null;

    private static IEnumerable<string> Data(JsonElement message) => !message.TryGetProperty("reply_markup", out var m) ? []
        : m.GetProperty("inline_keyboard").EnumerateArray().SelectMany(row => row.EnumerateArray()).Select(b => b.GetProperty("callback_data").GetString()!);

    private async Task<T> QueryAsync<T>(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public async Task Tapping_the_thread_button_starts_a_follow_and_sends_the_root_message_with_the_title_encoded()
    {
        var rig = Build();
        var root = await RootAsync(Hostile);

        await PressAsync(rig, $"f:{root}");

        var send = Assert.Single(Sends());
        Assert.StartsWith("🧵 Following <b>", Text(send), StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; &lt;b&gt;", Text(send), StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", Text(send), StringComparison.Ordinal);
        Assert.Contains("for 14 days", Text(send), StringComparison.Ordinal);
        Assert.Null(ReplyTo(send));
        Assert.Equal(["u:1"], Data(send));
        Assert.Equal(["🧵 Following for 14 days"], Answers());
        Assert.Equal(100, await QueryAsync<long>("select root_message_id from follows where id = 1"));
        Assert.Equal(14, await QueryAsync<int>("select extract(day from ends_at - started_at)::int from follows where id = 1"));
        Assert.Equal(root, await QueryAsync<long>("select root_item_id from follows where id = 1"));
    }

    [Fact]
    public async Task One_follow_per_story_even_from_another_member_of_it()
    {
        var rig = Build();
        var root = await RootAsync();
        var member = await LaterAsync("Same story elsewhere", TestVectors.OneHot(0), root);
        await PressAsync(rig, $"f:{root}");

        await PressAsync(rig, $"f:{member}");
        await PressAsync(rig, $"f:{root}");

        Assert.Single(Sends());
        Assert.Equal(1, await QueryAsync<int>("select count(*) from follows"));
        Assert.Equal(["🧵 Following for 14 days", "Already following this story", "Already following this story"], Answers());
    }

    [Fact]
    public async Task At_most_the_configured_number_of_stories_are_followed_and_the_answer_says_so()
    {
        var rig = Build(o => o.Follow = new FollowOptions { MaxActive = 2 });
        var a = await RootAsync("A", TestVectors.OneHot(0));
        var b = await RootAsync("B", TestVectors.OneHot(1));
        var c = await RootAsync("C", TestVectors.OneHot(2));

        await PressAsync(rig, $"f:{a}");
        await PressAsync(rig, $"f:{b}");
        await PressAsync(rig, $"f:{c}");

        Assert.Equal(2, Sends().Count);
        Assert.Equal("2 stories are followed already; stop one with /follows", Answers()[2]);
        Assert.Equal(2, await QueryAsync<int>("select count(*) from follows"));
    }

    [Fact]
    public async Task A_stopped_follow_frees_its_place()
    {
        var rig = Build(o => o.Follow = new FollowOptions { MaxActive = 1 });
        var a = await RootAsync("A", TestVectors.OneHot(0));
        var b = await RootAsync("B", TestVectors.OneHot(1));
        await PressAsync(rig, $"f:{a}");
        await PressAsync(rig, "u:1");

        await PressAsync(rig, $"f:{b}");

        Assert.Equal("Stopped following", Answers()[1]);
        Assert.Equal("🧵 Following for 14 days", Answers()[2]);
    }

    [Fact]
    public async Task With_following_off_the_button_answers_off_and_nothing_is_stored_or_sent()
    {
        var rig = Build(o => o.Follow = new FollowOptions { Enabled = false });
        var root = await RootAsync();

        await PressAsync(rig, $"f:{root}");

        Assert.Equal(["Following is off"], Answers());
        Assert.Empty(Sends());
        Assert.Equal(0, await QueryAsync<int>("select count(*) from follows"));
    }

    [Fact]
    public async Task Someone_elses_tap_changes_nothing()
    {
        var rig = Build();
        var root = await RootAsync();

        await PressAsync(rig, $"f:{root}", from: 999);

        Assert.Equal(["Not for you."], Answers());
        Assert.Empty(Sends());
        Assert.Equal(0, await QueryAsync<int>("select count(*) from follows"));
    }

    [Fact]
    public async Task A_tap_on_an_item_that_is_gone_says_so()
    {
        var rig = Build();

        await PressAsync(rig, "f:12345");

        Assert.Equal(["No such item"], Answers());
        Assert.Empty(Sends());
    }

    [Fact]
    public async Task A_root_message_that_cannot_be_sent_leaves_no_follow_behind()
    {
        var rig = Build();
        var root = await RootAsync();
        _sendFails = true;

        await Assert.ThrowsAsync<TelegramException>(() => PressAsync(rig, $"f:{root}"));

        Assert.Equal(0, await QueryAsync<int>("select count(*) from follows"));
    }

    [Fact]
    public async Task Cluster_members_and_near_duplicates_come_as_replies_to_the_root_and_nothing_else_does()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        var member = await LaterAsync("Member of the story", TestVectors.OneHot(5), root);
        var near = await LaterAsync("Near duplicate", TestVectors.Blend(0, 1, 0.41f), null);   // cosine 0.82: under the cluster threshold, over the follow's
        var far = await LaterAsync("Related only loosely", TestVectors.Blend(0, 1, 0.5f), null);   // cosine 0.71
        var other = await LaterAsync("Member of another story", TestVectors.OneHot(0), far);
        var pending = await LaterAsync("Not clustered yet", TestVectors.OneHot(0), null, considered: false);
        var earlier = await Seed.ItemAsync(pg, 2, "Ingested before the follow", TestVectors.OneHot(0), Now.AddHours(-3), Now.AddHours(-3));
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set clustered = true where id = @earlier", new { earlier });
        }

        var sent = await rig.Job.RunAsync(default);

        Assert.Equal(2, sent);
        var replies = Sends().Skip(1).ToList();
        Assert.All(replies, r => Assert.Equal(100, ReplyTo(r)));
        Assert.Contains("Member of the story", Text(replies[0]), StringComparison.Ordinal);
        Assert.Contains("Near duplicate", Text(replies[1]), StringComparison.Ordinal);
        Assert.Contains($"v:{member}:u", Data(replies[0]));
        Assert.Contains($"v:{member}:d", Data(replies[0]));
        Assert.Equal(2, await QueryAsync<int>("select sent from follows where id = 1"));
        Assert.Equal(near, await QueryAsync<long>("select last_item_id from follows where id = 1"));
        Assert.Equal(0, await QueryAsync<int>("select count(*) from follow_items where item_id in (@far, @other, @pending, @earlier)", new { far, other, pending, earlier }));
    }

    [Fact]
    public async Task An_item_close_to_one_already_sent_follows_it_and_nothing_is_sent_twice()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        await LaterAsync("First development", TestVectors.Blend(0, 1, 0.41f), null);
        await rig.Job.RunAsync(default);
        var second = await LaterAsync("Second development", TestVectors.Blend(0, 1, 0.7f), null);   // 0.39 to the root, 0.85 to the first development

        var again = await rig.Job.RunAsync(default);
        var third = await rig.Job.RunAsync(default);

        Assert.Equal(1, again);
        Assert.Equal(0, third);
        Assert.Equal(3, Sends().Count);
        Assert.Contains("Second development", Text(Sends()[2]), StringComparison.Ordinal);
        Assert.Equal(second, await QueryAsync<long>("select last_item_id from follows where id = 1"));
    }

    [Fact]
    public async Task Hostile_titles_are_encoded_in_updates_and_in_the_close()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        await LaterAsync(Hostile, TestVectors.OneHot(5), root);
        _summary = "Summary with <i>markup</i> & more";
        await rig.Job.RunAsync(default);

        _time.Advance(TimeSpan.FromDays(15));
        await rig.Job.RunAsync(default);

        foreach (var send in Sends().Skip(1))
        {
            Assert.DoesNotContain("<script>", Text(send), StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", Text(send), StringComparison.Ordinal);
        }

        Assert.Contains("Summary with &lt;i&gt;markup&lt;/i&gt; &amp; more", Text(Sends()[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_update_shows_the_read_summary_or_the_start_of_the_text_in_one_line()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        var read = await LaterAsync("With a read", TestVectors.OneHot(5), root);
        await Seed.ReadAsync(pg, read, "homelab", "fyi");
        var plain = await LaterAsync("Without a read", TestVectors.OneHot(6), root, minutes: 6);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set content = @content where id = @plain", new { plain, content = "First line.\n\n" + new string('x', 400) });
        }

        await rig.Job.RunAsync(default);

        var replies = Sends().Skip(1).Select(Text).ToList();
        Assert.EndsWith("\n\ns", replies[0], StringComparison.Ordinal);
        Assert.Contains("First line. xxx", replies[1], StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), replies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_send_keeps_the_item_for_the_next_run_and_does_not_stop_the_job()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        await LaterAsync("Member", TestVectors.OneHot(5), root);
        _sendFails = true;

        Assert.Equal(0, await rig.Job.RunAsync(default));
        Assert.Equal(0, await QueryAsync<int>("select count(*) from follow_items"));
        _sendFails = false;

        Assert.Equal(1, await rig.Job.RunAsync(default));
        Assert.Equal(1, await QueryAsync<int>("select count(*) from follow_items"));
    }

    [Fact]
    public async Task Quiet_hours_hold_the_run_and_the_close_until_they_end()
    {
        var rig = Build(o => o.Quiet = new QuietOptions { From = new TimeSpan(11, 0, 0), To = new TimeSpan(13, 0, 0) });
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        await LaterAsync("Member", TestVectors.OneHot(5), root);
        var rootSends = Sends().Count;

        await rig.Job.TickAsync(default);
        _time.Advance(TimeSpan.FromDays(15) - TimeSpan.FromHours(0.5));   // 11:30 local, past the end of the follow
        await rig.Job.TickAsync(default);

        Assert.Equal(rootSends, Sends().Count);
        Assert.Equal("active", await QueryAsync<string>("select status from follows where id = 1"));

        _time.Advance(TimeSpan.FromHours(2));   // 13:30 local
        await rig.Job.TickAsync(default);

        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
        Assert.Equal(rootSends + 2, Sends().Count);   // what waited, then the close
    }

    [Fact]
    public async Task At_the_end_of_the_window_the_follow_closes_with_a_summary_and_the_links()
    {
        var rig = Build();
        var root = await RootAsync("Root story");
        await PressAsync(rig, $"f:{root}");
        await LaterAsync("First development", TestVectors.OneHot(5), root);
        await LaterAsync("Second development", TestVectors.OneHot(6), root, minutes: 6);
        await rig.Job.RunAsync(default);
        _summary = "It started small and grew.";

        _time.Advance(TimeSpan.FromDays(14).Add(TimeSpan.FromMinutes(1)));
        await LaterAsync("Too late", TestVectors.OneHot(7), root, minutes: 60 * 24 * 15);
        await rig.Job.RunAsync(default);

        var close = Sends()[^1];
        Assert.Equal(100, ReplyTo(close));
        Assert.Contains("Story closed:</b> Root story", Text(close), StringComparison.Ordinal);
        Assert.Contains("It started small and grew.", Text(close), StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/", Text(close), StringComparison.Ordinal);
        Assert.Contains("First development", Text(close), StringComparison.Ordinal);
        Assert.Contains("Second development", Text(close), StringComparison.Ordinal);
        Assert.DoesNotContain("Too late", Text(close), StringComparison.Ordinal);
        Assert.False(close.TryGetProperty("reply_markup", out _));
        var chat = Assert.Single(_chatBodies);
        Assert.Contains("untrusted_page", chat, StringComparison.Ordinal);
        Assert.Contains("First development", chat, StringComparison.Ordinal);
        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
        Assert.Equal(0, await rig.Job.RunAsync(default));
        Assert.Equal(4, Sends().Count);   // root, two updates, the close: nothing after it
    }

    [Fact]
    public async Task When_the_model_fails_the_close_still_sends_the_links()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        await LaterAsync("First development", TestVectors.OneHot(5), root);
        await rig.Job.RunAsync(default);
        _modelDown = true;

        _time.Advance(TimeSpan.FromDays(15));
        await rig.Job.RunAsync(default);

        var close = Text(Sends()[^1]);
        Assert.Contains("Story closed:", close, StringComparison.Ordinal);
        Assert.Contains("First development", close, StringComparison.Ordinal);
        Assert.DoesNotContain("The story grew.", close, StringComparison.Ordinal);
        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
    }

    [Fact]
    public async Task A_story_with_nothing_new_closes_without_a_model_call()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");

        _time.Advance(TimeSpan.FromDays(15));
        await rig.Job.RunAsync(default);

        Assert.Contains("No later items matched this story.", Text(Sends()[^1]), StringComparison.Ordinal);
        Assert.Empty(_chatBodies);
    }

    [Fact]
    public async Task A_close_that_cannot_be_sent_stays_active_and_is_retried()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        _time.Advance(TimeSpan.FromDays(15));
        _sendFails = true;

        await rig.Job.RunAsync(default);
        Assert.Equal("active", await QueryAsync<string>("select status from follows where id = 1"));
        _sendFails = false;
        await rig.Job.RunAsync(default);

        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
    }

    [Fact]
    public async Task The_message_limit_closes_the_follow_and_says_why()
    {
        var rig = Build(o => o.Follow = new FollowOptions { MaxMessages = 3 });
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        for (var i = 0; i < 5; i++)
        {
            await LaterAsync($"Development {i}", TestVectors.OneHot(5 + i), root, minutes: 5 + i);
        }

        var sent = await rig.Job.RunAsync(default);
        await rig.Job.RunAsync(default);

        Assert.Equal(3, sent);
        Assert.Equal(3, await QueryAsync<int>("select sent from follows where id = 1"));
        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
        Assert.Equal(5, Sends().Count);   // root, three updates, the close
        Assert.Contains("Closed at the limit of 3 messages.", Text(Sends()[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Follows_lists_the_active_ones_with_a_stop_button_and_the_button_stops_one()
    {
        var rig = Build();
        var a = await RootAsync("Story <A>", TestVectors.OneHot(0));
        var b = await RootAsync("Story B", TestVectors.OneHot(1));
        await PressAsync(rig, $"f:{a}");
        await PressAsync(rig, $"f:{b}");
        _telegram.Clear();

        await rig.Commands.HandleAsync(new TgMessage(42, 42, "/follows"), default);
        var list = Assert.Single(Sends());
        Assert.Contains("Story &lt;A&gt;", Text(list), StringComparison.Ordinal);
        Assert.Contains("Story B", Text(list), StringComparison.Ordinal);
        Assert.Equal(["u:1", "u:2"], Data(list));

        await PressAsync(rig, "u:1");
        await PressAsync(rig, "u:1");

        Assert.Equal(["Stopped following", "Not following this any more"], Answers());
        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));
        Assert.Equal("active", await QueryAsync<string>("select status from follows where id = 2"));
    }

    [Fact]
    public async Task Follows_with_nothing_followed_says_how_to_start()
    {
        var rig = Build();

        await rig.Commands.HandleAsync(new TgMessage(42, 42, "/follows"), default);

        Assert.Contains("You follow no stories.", Text(Assert.Single(Sends())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unfollow_stops_the_only_follow_or_the_one_named_and_lists_when_it_is_ambiguous()
    {
        var rig = Build();
        var a = await RootAsync("Story A", TestVectors.OneHot(0));
        var b = await RootAsync("Story B", TestVectors.OneHot(1));
        await PressAsync(rig, $"f:{a}");
        _telegram.Clear();

        await rig.Commands.HandleAsync(new TgMessage(42, 42, "/unfollow"), default);
        Assert.Contains("Story closed:", Text(Assert.Single(Sends())), StringComparison.Ordinal);
        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 1"));

        await PressAsync(rig, $"f:{a}");
        await PressAsync(rig, $"f:{b}");
        _telegram.Clear();
        await rig.Commands.HandleAsync(new TgMessage(42, 42, "/unfollow"), default);
        Assert.Equal(["u:2", "u:3"], Data(Assert.Single(Sends())));

        _telegram.Clear();
        await rig.Commands.HandleAsync(new TgMessage(42, 42, "/unfollow 3"), default);
        Assert.Contains("Story closed:", Text(Assert.Single(Sends())), StringComparison.Ordinal);
        Assert.Equal("closed", await QueryAsync<string>("select status from follows where id = 3"));
        Assert.Equal("active", await QueryAsync<string>("select status from follows where id = 2"));
    }

    [Fact]
    public async Task Someone_else_cannot_list_or_stop_follows_by_command()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        _telegram.Clear();

        await rig.Commands.HandleAsync(new TgMessage(999, 999, "/follows"), default);
        await rig.Commands.HandleAsync(new TgMessage(999, 999, "/unfollow"), default);

        Assert.Empty(Sends());
        Assert.Equal("active", await QueryAsync<string>("select status from follows where id = 1"));
    }

    [Fact]
    public async Task A_reply_to_the_root_message_is_answered_with_the_help_text_and_not_searched()
    {
        var rig = Build();

        await rig.Commands.HandleAsync(new TgMessage(42, 42, "what is new?", ReplyToFollowId: 1), default);

        Assert.Equal(ReplyHandler.Help, Text(Assert.Single(Sends())));
        Assert.Empty(_chatBodies);
    }

    [Fact]
    public async Task A_reply_to_a_follow_update_acts_on_its_item()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        var member = await LaterAsync("Member", TestVectors.OneHot(5), root);
        await rig.Job.RunAsync(default);
        var update = Sends()[1];

        await rig.Commands.HandleAsync(new TgMessage(42, 42, "👍", ReplyToItemId: CallbackData.ItemIdOf(Data(update).First())), default);

        Assert.Equal(1, (await new ItemStore(pg.Db).GetAsync(member, default))!.Vote);
    }

    private List<string> EditedButtons() => _telegram.Where(t => t.Method == "editMessageReplyMarkup").Select(t => JsonSerializer.Deserialize<JsonElement>(t.Body))
        .SelectMany(b => b.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray().SelectMany(row => row.EnumerateArray()).Select(x => x.GetProperty("callback_data").GetString()!)).ToList();

    [Fact]
    public async Task A_vote_on_a_follow_update_does_not_add_the_thread_button_but_one_on_an_unfollowed_item_keeps_it()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        var member = await LaterAsync("Member", TestVectors.OneHot(5), null);   // matched by similarity, not by cluster
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into follow_items (follow_id, item_id) values (1, @member)", new { member });
        }

        var other = await Seed.ItemAsync(pg, 3, "Unrelated", TestVectors.OneHot(9));
        _telegram.Clear();
        await PressAsync(rig, $"v:{member}:u");
        var onUpdate = EditedButtons();
        _telegram.Clear();
        await PressAsync(rig, $"v:{root}:u");
        var onRoot = EditedButtons();
        _telegram.Clear();
        await PressAsync(rig, $"v:{other}:u");
        var onOther = EditedButtons();

        Assert.DoesNotContain($"f:{member}", onUpdate);
        Assert.Contains($"v:{member}:d", onUpdate);
        Assert.DoesNotContain($"f:{root}", onRoot);
        Assert.Contains($"f:{other}", onOther);
    }

    [Fact]
    public async Task A_muted_feed_is_not_followed_into()
    {
        var rig = Build();
        var root = await RootAsync();
        await PressAsync(rig, $"f:{root}");
        await LaterAsync("From a muted feed", TestVectors.OneHot(5), root, feed: 7);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update feeds set muted = true where id = 7");
        }

        Assert.Equal(0, await rig.Job.RunAsync(default));
    }
}
