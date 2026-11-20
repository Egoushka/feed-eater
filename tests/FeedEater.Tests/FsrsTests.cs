using FeedEater.Swipe;
using FeedEater.Inbox;

namespace FeedEater.Tests;

/// <summary>Golden values come from ts-fsrs 6.0.0 (MIT) with fuzz and short-term scheduling off.</summary>
public sealed class FsrsTests
{
    [Theory]
    [InlineData(1, 0.212, 6.4133)]
    [InlineData(2, 1.2931, 5.11217071)]
    [InlineData(3, 2.3065, 2.11810397)]
    [InlineData(4, 8.2956, 1.0)]
    public void The_first_review_starts_stability_and_difficulty_per_grade(int grade, double stability, double difficulty)
    {
        var state = Fsrs.Review(null, 0, grade);

        Assert.Equal(stability, state.Stability, 6);
        Assert.Equal(difficulty, state.Difficulty, 6);
    }

    [Theory]
    [InlineData(3, 3, 1, 0.63685069, 7.39450274)]
    [InlineData(3, 3, 2, 9.23487052, 4.75285849)]
    [InlineData(3, 3, 3, 13.82690327, 2.11121424)]
    [InlineData(3, 3, 4, 23.88306328, 1.0)]
    [InlineData(3, 10, 1, 0.75916016, 7.39450274)]
    [InlineData(3, 10, 3, 25.10871981, 2.11121424)]
    public void A_second_review_matches_ts_fsrs(int firstGrade, double elapsedDays, int grade, double stability, double difficulty)
    {
        var state = Fsrs.Review(Fsrs.Review(null, 0, firstGrade), elapsedDays, grade);

        Assert.Equal(stability, state.Stability, 5);
        Assert.Equal(difficulty, state.Difficulty, 5);
    }

    [Fact]
    public void Retrievability_is_one_at_once_and_the_retention_when_the_interval_has_passed()
    {
        Assert.Equal(1, Fsrs.Retrievability(0, 5), 8);
        Assert.Equal(0.9, Fsrs.Retrievability(5, 5), 6);
        Assert.True(Fsrs.Retrievability(30, 5) < Fsrs.Retrievability(10, 5));
    }

    [Theory]
    [InlineData(0.212, 1)]
    [InlineData(2.3065, 2)]
    [InlineData(13.82690327, 14)]
    [InlineData(8.2956, 8)]
    public void The_interval_is_the_stability_in_whole_days_and_at_least_one(double stability, int days) => Assert.Equal(days, Fsrs.Interval(stability));
}

public sealed class LabelsTests
{
    [Fact]
    public void Every_act_any_card_offers_has_a_label()
    {
        var acts = SwipeService.Acts
            .Concat(["good", "again", "send", "dismiss", "idea_keep", "idea_archive", "idea_later", "idea_promote"])
            .Concat(new[] { InboxKinds.Follow, InboxKinds.Duel, InboxKinds.Idea, InboxKinds.Resurface, InboxKinds.Release, InboxKinds.Review, InboxKinds.Autopsy, InboxKinds.Notice }
                .SelectMany(InboxCards.ActsFor))
            .Distinct().ToList();

        Assert.All(acts, a => Assert.NotNull(Labels.Of(a)));
    }

    [Theory]
    [InlineData("save", "kept")]
    [InlineData("good", "kept")]
    [InlineData("dislike", "dismissed")]
    [InlineData("again", "snoozed")]
    [InlineData("idea_later", "snoozed")]
    [InlineData("open", "opened")]
    [InlineData("idea", "promoted")]
    [InlineData("idea_promote", "promoted")]
    public void An_act_maps_to_one_label(string act, string label) => Assert.Equal(label, Labels.Of(act));

    [Fact]
    public void An_unknown_act_has_no_label() => Assert.Null(Labels.Of("nonsense"));
}

public sealed class IdeaPickerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static IdeaItem Idea(long id, string status = "active", int reps = 0, double stability = 0, int lastDaysAgo = 0) => new()
    {
        Id = id,
        Status = status,
        Reps = reps,
        Stability = stability,
        LastReviewAt = reps == 0 ? null : Now.AddDays(-lastDaysAgo).UtcDateTime,
    };

    [Fact]
    public void The_idea_closest_to_forgotten_comes_first_and_a_new_one_counts_as_forgotten()
    {
        var picked = IdeaPicker.Pick([Idea(1, reps: 1, stability: 5, lastDaysAgo: 6), Idea(2, reps: 1, stability: 5, lastDaysAgo: 30), Idea(3)], Now, 3);

        Assert.Equal([3L, 2, 1], picked.Select(i => i.Id));
    }

    [Fact]
    public void Archived_and_promoted_ideas_never_come_back_and_an_idea_comes_once()
    {
        var picked = IdeaPicker.Pick([Idea(1, "archived"), Idea(2, "promoted"), Idea(3), Idea(3)], Now, 5);

        Assert.Equal([3L], picked.Select(i => i.Id));
    }

    [Fact]
    public void No_slots_pick_nothing() => Assert.Empty(IdeaPicker.Pick([Idea(1)], Now, 0));

    [Fact]
    public void The_seed_holds_nine_distinct_ideas_in_his_words()
    {
        Assert.Equal(9, LostIdeaSeed.All.Count);
        Assert.Equal(9, LostIdeaSeed.All.Select(s => s.Key).Distinct().Count());
        Assert.Contains(LostIdeaSeed.All, s => s.Body == "okay record them + cached code idea" && s.Session == "2a685036");
    }
}
