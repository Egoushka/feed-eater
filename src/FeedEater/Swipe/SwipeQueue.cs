namespace FeedEater.Swipe;

public sealed record QueueCandidate(long Id, double Score, string? Category);

public sealed record QueuedCard(long Id, string? Category);

/// <summary>The cards of one session, chosen before it starts. Pure.</summary>
public static class SwipeQueue
{
    /// <summary>
    /// Each category with a share gets that share of <paramref name="cards"/> from its own best-scored candidates; the rest of the slots go to the
    /// best-scored candidates left, whatever their category. The picks are then shuffled with <paramref name="seed"/> so categories interleave.
    /// </summary>
    public static IReadOnlyList<QueuedCard> Build(IReadOnlyList<QueueCandidate> candidates, IReadOnlyDictionary<string, double> shares, int cards, int seed)
    {
        var picked = new List<QueueCandidate>();
        var taken = new HashSet<long>();
        var byScore = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Id).ToList();

        var total = shares.Values.Sum();
        var scale = total > 1 ? 1 / total : 1;
        foreach (var (category, share) in shares)
        {
            var slots = (int)Math.Round(cards * share * scale, MidpointRounding.AwayFromZero);
            foreach (var c in byScore.Where(c => c.Category == category).Take(slots))
            {
                if (picked.Count < cards && taken.Add(c.Id))
                {
                    picked.Add(c);
                }
            }
        }

        foreach (var c in byScore)
        {
            if (picked.Count >= cards)
            {
                break;
            }

            if (taken.Add(c.Id))
            {
                picked.Add(c);
            }
        }

        var random = new Random(seed);
        return picked.OrderBy(_ => random.Next()).Select(c => new QueuedCard(c.Id, c.Category)).ToList();
    }
}
