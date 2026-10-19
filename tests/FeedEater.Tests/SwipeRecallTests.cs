using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Llm;
using FeedEater.Storage;
using FeedEater.Swipe;

namespace FeedEater.Tests;

public sealed class RecallSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_good_answer_climbs_the_ladder_and_a_forgotten_one_starts_again_tomorrow()
    {
        var step = 0;
        var days = new List<int>();
        for (var i = 0; i < 7; i++)
        {
            var (next, due) = RecallScheduler.Next(step, true, Now);
            days.Add((int)(due - Now).TotalDays);
            step = next;
        }

        Assert.Equal([1, 3, 7, 16, 35, 35, 35], days);
        Assert.Equal((0, Now.AddDays(1)), RecallScheduler.Next(4, false, Now));
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, true, true)]
    [InlineData(3, false, false)]
    public void Only_a_good_answer_on_a_card_answered_before_counts_as_remembered(int stepBefore, bool good, bool remembered) =>
        Assert.Equal(remembered, RecallScheduler.Remembered(stepBefore, good));
}

public sealed class SwipeContentParsingTests
{
    [Fact]
    public void Cards_are_read_from_the_first_json_object_and_capped_at_three()
    {
        var pairs = SwipeContent.ParseCards("""Sure! {"cards":[{"q":"Q1","a":"A1"},{"q":"Q2","a":"A2"},{"q":"Q3","a":"A3"},{"q":"Q4","a":"A4"}]}""");

        Assert.Equal(["Q1", "Q2", "Q3"], pairs.Select(p => p.Question));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"cards":"nope"}""")]
    [InlineData("""{"cards":[{"q":"only a question"},{"a":"only an answer"},{"q":"","a":"x"}]}""")]
    [InlineData("""{"cards":[]}""")]
    public void Anything_that_is_not_a_card_gives_no_cards(string reply) => Assert.Empty(SwipeContent.ParseCards(reply));

    [Fact]
    public void An_angle_is_one_trimmed_sentence_and_empty_means_none()
    {
        Assert.Equal("Argue X.", SwipeContent.ParseAngle("""{"angle":"  Argue X.  "}"""));
        Assert.Null(SwipeContent.ParseAngle("""{"angle":""}"""));
        Assert.Null(SwipeContent.ParseAngle("no json"));
    }

    [Fact]
    public void A_note_becomes_a_question_with_its_points_as_the_answer()
    {
        var pair = SwipeContent.FromNote("table-tennis", "Practice", ["Relax the grip.", "Use the whole body."]);

        Assert.Equal("What did you note about table tennis? (Practice)", pair.Question);
        Assert.Equal("• Relax the grip.\n• Use the whole body.", pair.Answer);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class SwipeContentJobTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Chat(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private (SwipeContentJob Job, StubHandler Llm) Job(FeedEaterOptions o, string? notesJson = null, Func<string, string>? reply = null)
    {
        var options = Options.Create(o);
        var llm = new StubHandler((request, body) => request.RequestUri!.AbsolutePath.EndsWith("embeddings", StringComparison.Ordinal)
            ? StubHandler.Json(TestVectors.EmbeddingResponse(JsonDocument.Parse(body).RootElement.GetProperty("input").GetArrayLength()))
            : StubHandler.Json(Chat((reply ?? (_ => """{"cards":[{"q":"What is X?","a":"Y."}]}"""))(body))));
        var notes = new StubHandler((_, _) => StubHandler.Json(notesJson ?? """{"items":[],"nextBefore":null}"""));
        var services = new ServiceCollection().AddSingleton(new NotesClient(notes.Client("http://notes/"))).BuildServiceProvider();
        var time = new FakeTimeProvider(Now);
        return (new SwipeContentJob(new RecallStore(pg.Db), new CategoryStore(pg.Db), new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            services, options, new LoopHealth(time), time, NullLogger<SwipeContentJob>.Instance), llm);
    }

    private static FeedEaterOptions Options_(Action<SwipeOptions>? tweak = null)
    {
        var o = new FeedEaterOptions { ProfilePath = "/nope/profile.json" };
        o.Swipe.Enabled = true;
        tweak?.Invoke(o.Swipe);
        return o;
    }

    private async Task<long> LikedItemAsync(string title, int dim)
    {
        var id = await Seed.ItemAsync(pg, 1, title, TestVectors.OneHot(dim), Now.UtcDateTime, content: "Long text about " + title);
        await new FeedbackStore(pg.Db).SetVoteAsync(id, 1, default);
        return id;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException("no row");
    }

    [Fact]
    public async Task A_liked_item_becomes_recall_cards_once_and_a_seed()
    {
        await LikedItemAsync("Liked", 3);
        var (job, llm) = Job(Options_(), reply: body => body.Contains("flash cards", StringComparison.Ordinal) ? """{"cards":[{"q":"What is X?","a":"Y."}]}""" : """{"angle":"Argue that X beats Z."}""");

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from recall_cards where source = 'item'"));
        Assert.Equal("Argue that X beats Z.", await ScalarAsync<string>("select angle from post_seeds"));
        Assert.Equal(2, llm.Calls.Count);   // one for the cards, one for the seed; the second tick asks nothing
    }

    [Fact]
    public async Task The_per_day_caps_hold()
    {
        for (var i = 0; i < 4; i++)
        {
            await LikedItemAsync($"Liked {i}", i);
        }

        var (job, _) = Job(Options_(s => { s.Recall.PerDay = 2; s.Seeds.PerDay = 1; }), reply: _ => """{"cards":[{"q":"a","a":"b"}],"angle":"An angle."}""");

        await job.TickAsync(default);

        Assert.Equal(2, await ScalarAsync<int>("select count(*)::int from recall_done where ref like 'item:%'"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from post_seeds where status = 'new'"));
    }

    [Fact]
    public async Task An_item_with_no_cards_or_no_angle_is_not_asked_about_again()
    {
        await LikedItemAsync("Nothing worth it", 5);
        var (job, llm) = Job(Options_(), reply: _ => """{"cards":[],"angle":""}""");

        await job.TickAsync(default);
        var calls = llm.Calls.Count;
        await job.TickAsync(default);

        Assert.Equal(calls, llm.Calls.Count);
        Assert.Equal(0, await ScalarAsync<int>("select count(*)::int from recall_cards"));
        Assert.Equal("dismissed", await ScalarAsync<string>("select status from post_seeds"));
    }

    [Fact]
    public async Task Switching_recall_and_seeds_off_asks_the_model_nothing()
    {
        await LikedItemAsync("Liked", 3);
        var (job, llm) = Job(Options_(s => { s.Recall.Enabled = false; s.Seeds.Enabled = false; }));

        await job.TickAsync(default);

        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task Notes_become_cards_without_a_chat_call_and_only_once()
    {
        const string notes = """{"items":[{"id":"n1","topic":"table-tennis","conversationTitle":"Practice","points":["Relax the grip."]},{"id":"n2","topic":"cooking","points":[]}],"nextBefore":null}""";
        var (job, llm) = Job(Options_(s => { s.Notes.BaseUrl = "http://notes/"; s.Notes.Token = "t"; }), notes);

        await job.TickAsync(default);
        await job.TickAsync(default);

        Assert.Equal(1, await ScalarAsync<int>("select count(*)::int from recall_cards where source = 'note'"));
        Assert.Equal("What did you note about table tennis? (Practice)", await ScalarAsync<string>("select front from recall_cards"));
        Assert.All(llm.Calls, c => Assert.DoesNotContain("chat/completions", c.Uri, StringComparison.Ordinal));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class RecallStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Only_due_cards_are_offered_oldest_first_and_an_answer_reschedules_the_card()
    {
        var store = new RecallStore(pg.Db);
        await store.AddCardsAsync("note", "note:a", null, null, [new RecallPair("Q-late", "A")], Now.AddHours(5), default);
        await store.AddCardsAsync("note", "note:b", null, null, [new RecallPair("Q-early", "A")], Now.AddHours(-5), default);
        await store.AddCardsAsync("note", "note:c", null, null, [new RecallPair("Q-mid", "A")], Now.AddHours(-1), default);

        var due = await store.DueAsync(Now, 10, default);
        Assert.Equal(["Q-early", "Q-mid"], due.Select(c => c.Front));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var session = await c.ExecuteScalarAsync<long>("insert into swipe_sessions (card_cap, time_cap_s) values (40, 900) returning id");
        await store.ReviewAsync(due[0], session, good: true, Now, default);
        await store.ReviewAsync(due[1], session, good: false, Now, default);

        Assert.Empty(await store.DueAsync(Now.AddHours(4), 10, default));
        var after = (await store.DueAsync(Now.AddHours(25), 10, default)).Select(x => x.Front).ToList();
        Assert.Equal("Q-late", after[0]);   // due first, at +5 h
        Assert.Contains("Q-early", after);   // good at step 0: back in a day
        Assert.Contains("Q-mid", after);     // forgotten: back tomorrow
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select step from recall_cards where front = 'Q-early'"));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select lapses from recall_cards where front = 'Q-mid'"));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("select count(*)::int from recall_reviews"));
    }
}
