using FeedEater.Storage;

namespace FeedEater.Ranking;

public sealed record Scored(long ItemId, double Score, string? ProfileKey);

/// <summary>score = best profile fit + Taste * (cos C+ - cos C-) + Prior * (feed 👍 rate - 0.5) [+ Learned * (p - 0.5) when the learned term is on]. Vectors are unit length.</summary>
public static class Scorer
{
    public static Scored Score(long itemId, long? feedId, float[] x, IReadOnlyList<Profile> profiles, Taste taste, WeightsOptions w)
    {
        string? key = null;
        var fit = 0d;
        foreach (var p in profiles)
        {
            var s = Vectors.Dot(x, p.Embedding);
            if (key is null || s > fit)
            {
                fit = s;
                key = p.Key;
            }
        }

        var liking = taste.Positive is null ? 0 : Vectors.Dot(x, taste.Positive) - (taste.Negative is null ? 0 : Vectors.Dot(x, taste.Negative));
        var prior = feedId is { } f && taste.FeedUpRates.TryGetValue(f, out var rate) ? rate - 0.5 : 0;
        var learned = taste.Learned is { } model ? w.Learned * (model.Probability(x) - 0.5) : 0;
        return new Scored(itemId, fit + (w.Taste * liking) + (w.Prior * prior) + learned, key);
    }
}
