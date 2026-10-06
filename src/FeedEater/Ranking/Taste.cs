using FeedEater.Storage;

namespace FeedEater.Ranking;

/// <summary>What the reader's reactions say so far: centroids of liked and disliked items, and each feed's 👍 rate.</summary>
public sealed record Taste(float[]? Positive, float[]? Negative, IReadOnlyDictionary<long, double> FeedUpRates, LogisticModel? Learned = null)
{
    public static Taste Empty { get; } = new(null, null, new Dictionary<long, double>());

    public static Taste Build(
        IReadOnlyList<float[]> positives, IReadOnlyList<float[]> negatives, IReadOnlyList<FeedVotes> feeds, WeightsOptions w) =>
        Build(positives.Select(v => new WeightedVector(v, 1)).ToList(), negatives.Select(v => new WeightedVector(v, 1)).ToList(), feeds, w);

    /// <summary>The centroids are weighted means; "enough" counts vectors, not weights.</summary>
    public static Taste Build(
        IReadOnlyList<WeightedVector> positives, IReadOnlyList<WeightedVector> negatives, IReadOnlyList<FeedVotes> feeds, WeightsOptions w)
    {
        var enough = positives.Count >= w.MinPositives;
        return new Taste(
            enough ? Vectors.WeightedCentroid(positives) : null,
            enough ? Vectors.WeightedCentroid(negatives) : null,
            feeds.Where(f => f.Up + f.Down >= w.MinFeedVotes).ToDictionary(f => f.FeedId, f => (double)f.Up / (f.Up + f.Down)));
    }
}
