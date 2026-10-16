using FeedEater.Ranking;

namespace FeedEater.Swipe;

/// <summary><paramref name="Embedding"/> is only needed to pick explore cards that differ from the rest; <paramref name="Predicted"/> is the yield model's probability, when there is a model.</summary>
public sealed record QueueCandidate(long Id, double Score, string? Category, float[]? Embedding = null, double? Predicted = null);

public sealed record QueuedCard(long Id, string? Category, string Source = SwipeQueue.Exploit, double? Predicted = null);

/// <summary>The cards of one session, chosen before it starts. Pure.</summary>
public static class SwipeQueue
{
    public const string Exploit = "exploit";
    public const string Explore = "explore";

    /// <summary>Explore cards come from outside the best-scored part of the pool, so they are not what the ranking would have shown anyway.</summary>
    private const double TopShare = 0.3;
    private const int ExploreSample = 10;

    /// <summary>
    /// Each category with a share gets that share of the exploit slots from its own best-scored candidates; the rest of the slots go to the
    /// best-scored candidates left, whatever their category. Each of the <paramref name="cards"/> slots is an explore slot with probability
    /// <paramref name="epsilon"/>: a random sample of ten candidates from outside the top 30% by score, of which the one least like the cards
    /// already picked is taken. The picks are then shuffled with <paramref name="seed"/> so categories and sources interleave.
    /// </summary>
    public static IReadOnlyList<QueuedCard> Build(
        IReadOnlyList<QueueCandidate> candidates, IReadOnlyDictionary<string, double> shares, int cards, int seed, double epsilon = 0)
    {
        var random = new Random(seed);
        var byScore = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Id).ToList();
        var exploreSlots = epsilon > 0 ? Enumerable.Range(0, cards).Count(_ => random.NextDouble() < epsilon) : 0;

        var taken = new HashSet<long>();
        var picked = PickExploit(byScore, shares, cards - exploreSlots, taken);
        var queue = picked.Select(c => new QueuedCard(c.Id, c.Category, Exploit, c.Predicted)).ToList();

        var pool = byScore.Skip((int)Math.Ceiling(byScore.Count * TopShare)).Where(c => !taken.Contains(c.Id)).ToList();
        for (var i = 0; i < exploreSlots && pool.Count > 0; i++)
        {
            var sample = pool.OrderBy(_ => random.Next()).Take(ExploreSample).ToList();
            var choice = sample.MinBy(c => Closeness(c, picked)) ?? sample[0];
            pool.Remove(choice);
            taken.Add(choice.Id);
            picked.Add(choice);
            queue.Add(new QueuedCard(choice.Id, choice.Category, Explore, choice.Predicted));
        }

        // An empty explore pool leaves slots open: give them to the best candidates left.
        foreach (var c in byScore)
        {
            if (queue.Count >= cards)
            {
                break;
            }

            if (taken.Add(c.Id))
            {
                queue.Add(new QueuedCard(c.Id, c.Category, Exploit, c.Predicted));
            }
        }

        return queue.OrderBy(_ => random.Next()).ToList();
    }

    private static List<QueueCandidate> PickExploit(List<QueueCandidate> byScore, IReadOnlyDictionary<string, double> shares, int cards, HashSet<long> taken)
    {
        var picked = new List<QueueCandidate>();
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

        return picked;
    }

    /// <summary>The highest similarity to any card picked so far; without embeddings every candidate is equally far.</summary>
    private static double Closeness(QueueCandidate c, List<QueueCandidate> picked) =>
        c.Embedding is null || picked.Count == 0
            ? 0
            : picked.Where(p => p.Embedding is not null).Select(p => Vectors.Dot(c.Embedding, p.Embedding!)).DefaultIfEmpty(0).Max();
}
