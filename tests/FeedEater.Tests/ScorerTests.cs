using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class ScorerTests
{
    private static readonly WeightsOptions W = new();

    private static Profile P(string key, int dim) => new() { Key = key, Kind = "project", Description = key, Embedding = TestVectors.OneHot(dim) };

    [Fact]
    public void Picks_the_closest_profile()
    {
        var s = Scorer.Score(1, null, TestVectors.Blend(1, 0, 0.2f), [P("a", 0), P("b", 1)], Taste.Empty, W);

        Assert.Equal("b", s.ProfileKey);
        Assert.True(s.Score > 0.9);
    }

    [Fact]
    public void No_profiles_means_no_key_and_zero_fit()
    {
        var s = Scorer.Score(1, null, TestVectors.OneHot(0), [], Taste.Empty, W);

        Assert.Null(s.ProfileKey);
        Assert.Equal(0, s.Score);
    }

    [Fact]
    public void Taste_lifts_items_like_past_positives_and_lowers_items_like_negatives()
    {
        var taste = new Taste(TestVectors.OneHot(5), TestVectors.OneHot(6), new Dictionary<long, double>());
        var profiles = new[] { P("a", 0) };

        var liked = Scorer.Score(1, null, TestVectors.Blend(0, 5, 0.5f), profiles, taste, W);
        var neutral = Scorer.Score(2, null, TestVectors.Blend(0, 7, 0.5f), profiles, taste, W);
        var disliked = Scorer.Score(3, null, TestVectors.Blend(0, 6, 0.5f), profiles, taste, W);

        Assert.True(liked.Score > neutral.Score);
        Assert.True(neutral.Score > disliked.Score);
    }

    [Fact]
    public void Feed_prior_adds_weight_times_rate_minus_half()
    {
        var taste = new Taste(null, null, new Dictionary<long, double> { [7] = 0.9 });
        var x = TestVectors.OneHot(0);

        var withPrior = Scorer.Score(1, 7, x, [P("a", 0)], taste, W);
        var without = Scorer.Score(2, 8, x, [P("a", 0)], taste, W);

        Assert.Equal(0.2 * 0.4, withPrior.Score - without.Score, 6);
    }

    [Fact]
    public void Taste_needs_enough_positives_and_feed_votes()
    {
        var nine = Enumerable.Range(0, 9).Select(TestVectors.OneHot).ToList();
        var ten = Enumerable.Range(0, 10).Select(TestVectors.OneHot).ToList();
        var feeds = new[] { new FeedVotes { FeedId = 1, Up = 3, Down = 1 }, new FeedVotes { FeedId = 2, Up = 4, Down = 1 } };

        var few = Taste.Build(nine, [TestVectors.OneHot(20)], feeds, W);
        var enough = Taste.Build(ten, [TestVectors.OneHot(20)], feeds, W);

        Assert.Null(few.Positive);
        Assert.Null(few.Negative);
        Assert.NotNull(enough.Positive);
        Assert.NotNull(enough.Negative);
        Assert.False(enough.FeedUpRates.ContainsKey(1));
        Assert.Equal(0.8, enough.FeedUpRates[2], 6);
    }
}

public sealed class RankSignalsTests
{
    [Fact]
    public void Parts_sum_to_the_score()
    {
        var taste = new Taste(TestVectors.OneHot(5), TestVectors.OneHot(6), new Dictionary<long, double> { [7] = 0.9 });
        var s = Scorer.Score(1, 7, TestVectors.Blend(0, 5, 0.5f), [new Profile { Key = "a", Kind = "project", Description = "a", Embedding = TestVectors.OneHot(0) }], taste, new WeightsOptions());

        Assert.NotNull(s.Signals);
        Assert.Equal(s.Score, s.Signals!.Fit + s.Signals.Taste + s.Signals.Prior + s.Signals.Learned, 9);
    }

    [Fact]
    public void Line_names_profile_and_nonzero_parts_with_signs()
    {
        Assert.Equal("fits chargehand 0.62 · liked-like +0.10 · feed 👍 −0.03", new RankSignals("chargehand", 0.62, 0.10, -0.03, 0).Line());
    }

    [Fact]
    public void Line_is_empty_without_profile_and_parts() => Assert.Equal("", new RankSignals(null, 0, 0, 0, 0).Line());
}
