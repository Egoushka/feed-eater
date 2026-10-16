using FeedEater.Swipe;

namespace FeedEater.Tests;

public sealed class SwipePolicyTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SwipeOptions O = new() { Minutes = 15, CooldownMinutes = 180, SessionsPerDay = 3 };

    private static SessionRow Open(DateTimeOffset startedAt) => new() { Id = 1, StartedAt = startedAt.UtcDateTime, CardCap = 40, TimeCapS = 900 };

    private static SessionRow Ended(DateTimeOffset endedAt, bool rated = false) =>
        Open(endedAt.AddMinutes(-10)) with { EndedAt = endedAt.UtcDateTime, EndReason = "cards", FeltAt = rated ? endedAt.UtcDateTime : null, Felt = rated ? (short)1 : null };

    [Fact]
    public void No_session_yet_starts_one() =>
        Assert.Equal(Gate.Start, SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, null, 0).Gate);

    [Fact]
    public void An_open_session_resumes_until_its_time_is_up()
    {
        Assert.Equal(Gate.Resume, SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, Open(Noon.AddMinutes(-14)), 1).Gate);
        Assert.Equal(Gate.Expire, SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, Open(Noon.AddMinutes(-15)), 1).Gate);
    }

    [Fact]
    public void An_expired_session_ends_at_its_cap_not_when_someone_opened_the_page() =>
        Assert.Equal(Noon.AddMinutes(-45).AddMinutes(15), SessionPolicy.EndOfExpired(Open(Noon.AddMinutes(-45))));

    [Fact]
    public void The_summary_is_shown_again_for_ten_minutes_until_it_is_rated()
    {
        Assert.Equal(Gate.Summary, SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, Ended(Noon.AddMinutes(-9)), 1).Gate);
        Assert.Equal(Gate.Closed, SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, Ended(Noon.AddMinutes(-11)), 1).Gate);
        Assert.Equal(Gate.Closed, SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, Ended(Noon.AddMinutes(-9), rated: true), 1).Gate);
    }

    [Fact]
    public void The_cooldown_holds_until_it_has_passed()
    {
        var held = SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, Ended(Noon.AddMinutes(-60), rated: true), 1);

        Assert.Equal(Gate.Closed, held.Gate);
        Assert.Equal(Noon.AddMinutes(120), held.Until);
        Assert.Equal(Gate.Start, SessionPolicy.Decide(O, Noon, TimeZoneInfo.Utc, Ended(Noon.AddMinutes(-181), rated: true), 1).Gate);
    }

    [Fact]
    public void The_daily_limit_holds_until_local_midnight()
    {
        var kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");   // UTC+3 in October 2026
        var held = SessionPolicy.Decide(O, Noon, kyiv, Ended(Noon.AddHours(-5), rated: true), 3);

        Assert.Equal(Gate.Closed, held.Gate);
        Assert.Equal("daily", held.Why);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 21, 0, 0, TimeSpan.Zero), held.Until);
    }
}

public sealed class SwipeQueueTests
{
    private static List<QueueCandidate> Pool() =>
    [
        .. Enumerable.Range(1, 20).Select(i => new QueueCandidate(i, 100 - i, "work")),
        .. Enumerable.Range(21, 20).Select(i => new QueueCandidate(i, 50 - i, "humour")),
        .. Enumerable.Range(41, 10).Select(i => new QueueCandidate(i, 200 - (i - 41), null)),
    ];

    [Fact]
    public void Shares_decide_how_many_cards_each_category_gets_and_the_best_of_each_are_taken()
    {
        var queue = SwipeQueue.Build(Pool(), new Dictionary<string, double> { ["humour"] = 0.5, ["work"] = 0.25 }, 20, 1);

        Assert.Equal(20, queue.Count);
        Assert.Equal(10, queue.Count(q => q.Category == "humour"));
        Assert.Equal(5, queue.Count(q => q.Category == "work"));
        Assert.Equal(5, queue.Count(q => q.Category is null));
        Assert.Equal(Enumerable.Range(21, 10).Select(i => (long)i).Order(), queue.Where(q => q.Category == "humour").Select(q => q.Id).Order());
    }

    [Fact]
    public void Slots_without_a_share_go_to_the_best_scores_left_and_no_card_repeats()
    {
        var queue = SwipeQueue.Build(Pool(), new Dictionary<string, double> { ["humour"] = 0.5 }, 20, 1);

        Assert.Equal(queue.Count, queue.Select(q => q.Id).Distinct().Count());
        Assert.Equal(10, queue.Count(q => q.Category == "humour"));
        Assert.Equal(10, queue.Count(q => q.Category is null));   // the best scores left over
    }

    [Fact]
    public void A_category_short_of_candidates_hands_its_slots_to_the_rest()
    {
        var pool = new List<QueueCandidate> { new(1, 1, "humour"), new(2, 5, "work"), new(3, 4, "work") };

        var queue = SwipeQueue.Build(pool, new Dictionary<string, double> { ["humour"] = 0.9 }, 3, 1);

        Assert.Equal([1L, 2L, 3L], queue.Select(q => q.Id).Order());
    }

    [Fact]
    public void Shares_above_one_in_total_are_scaled_down_and_fewer_candidates_than_cards_is_fine()
    {
        var queue = SwipeQueue.Build(Pool().Take(4).ToList(), new Dictionary<string, double> { ["work"] = 0.8, ["humour"] = 0.8 }, 40, 1);

        Assert.Equal(4, queue.Count);
    }

    [Fact]
    public void The_same_seed_gives_the_same_order()
    {
        var shares = new Dictionary<string, double> { ["humour"] = 0.5 };

        Assert.Equal(SwipeQueue.Build(Pool(), shares, 20, 7), SwipeQueue.Build(Pool(), shares, 20, 7));
    }
}

public sealed class CategoriesFileTests
{
    [Fact]
    public void A_missing_file_is_no_categories() =>
        Assert.Empty(CategoriesFile.LoadOrNone("/no/such/categories.json").Categories);

    [Fact]
    public void The_example_file_shipped_in_config_parses()
    {
        var file = CategoriesFile.Parse(File.ReadAllText(RepoRoot.File("config/categories.example.json")));

        Assert.NotEmpty(file.Categories);
    }

    [Theory]
    [InlineData("""{"categories":[{"key":"a","description":"x"},{"key":"a","description":"y"}]}""", "twice")]
    [InlineData("""{"categories":[{"key":"a","description":""}]}""", "key and a description")]
    [InlineData("""{"categories":[{"key":"a","description":"x","share":1.5}]}""", "between 0 and 1")]
    [InlineData("""{"categories":[{"key":"a","description":"x","share":0.7},{"key":"b","description":"y","share":0.7}]}""", "more than 1")]
    [InlineData("""{"categories":[{"key":"a","description":"x","events":["dwell"]}]}""", "not an event")]
    public void An_invalid_file_says_what_is_wrong(string json, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => CategoriesFile.Parse(json)).Message, StringComparison.Ordinal);

    [Fact]
    public void Dwell_is_not_something_a_category_can_count_as_yield() =>
        Assert.DoesNotContain("dwell", CategoriesFile.YieldEvents);

    [Fact]
    public void Ranking_code_never_mentions_dwell()
    {
        var files = Directory.GetFiles(RepoRoot.File("src/FeedEater/Ranking"), "*.cs").Append(RepoRoot.File("src/FeedEater/Storage/FeedbackStore.cs"));

        Assert.All(files, f => Assert.DoesNotContain("dwell", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase));
    }
}
