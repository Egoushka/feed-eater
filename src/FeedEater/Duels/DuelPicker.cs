using FeedEater.Ranking;

namespace FeedEater.Duels;

public sealed record DuelItem
{
    public long Id { get; init; }
    public long StoryId { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public double Score { get; init; }
    public float[] Embedding { get; init; } = [];
}

/// <summary>
/// Chooses the two items a duel asks about. They come from the middle of the score range, where the ranking is least sure, and
/// are the least alike there, so the answer says something about taste rather than about one topic.
/// </summary>
public static class DuelPicker
{
    public const int MinPool = 6;

    /// <summary>
    /// One item per story (the first in input order), then the items from the 40th to the 60th percentile of the pool by score
    /// (rank / (n - 1)); a pool whose band holds fewer than two takes the two ranks at its middle. Of the pairs in the band that are
    /// not in <paramref name="seen"/>, the one with the lowest cosine similarity wins. Null for fewer than <see cref="MinPool"/> items or no pair left.
    /// </summary>
    public static (DuelItem First, DuelItem Second)? Pick(IReadOnlyList<DuelItem> eligible, IReadOnlySet<(long, long)> seen)
    {
        var pool = eligible.DistinctBy(i => i.StoryId).OrderBy(i => i.Score).ThenBy(i => i.Id).ToList();
        if (pool.Count < MinPool)
        {
            return null;
        }

        var last = pool.Count - 1;
        var band = Enumerable.Range(0, pool.Count).Where(r => 5 * Math.Abs(2 * r - last) <= last).ToList();
        if (band.Count < 2)
        {
            band = [last / 2, last / 2 + 1];
        }

        (DuelItem, DuelItem)? best = null;
        var bestSimilarity = double.MaxValue;
        for (var x = 0; x < band.Count; x++)
        {
            for (var y = x + 1; y < band.Count; y++)
            {
                DuelItem first = pool[band[x]], second = pool[band[y]];
                if (seen.Contains(Pair(first.Id, second.Id)))
                {
                    continue;
                }

                var similarity = Vectors.Dot(Vectors.Normalize(first.Embedding), Vectors.Normalize(second.Embedding));
                if (similarity < bestSimilarity)
                {
                    bestSimilarity = similarity;
                    best = (first, second);
                }
            }
        }

        return best;
    }

    /// <summary>The unordered pair as stored in <paramref name="seen"/>.</summary>
    public static (long, long) Pair(long a, long b) => a < b ? (a, b) : (b, a);
}
