using Dapper;
using Microsoft.Extensions.Options;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Swipe;

/// <summary>
/// What counts as yield. Only acts that cost effort or show lasting value; the time spent on a card, going on and going back are worth
/// nothing here, by design: the objective is the opposite of the apps this replaces. Nothing in this file may read the dwell column.
/// </summary>
public static class YieldLabels
{
    /// <summary>A card counts as a yield example (the positive class) when its acts add up to at least this.</summary>
    public const double PositiveAt = 0.5;

    private static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["idea"] = 1.0,
        ["share"] = 0.8,
        ["save"] = 0.7,
        ["recall"] = 0.7,
        ["like"] = 0.3,
        ["open"] = 0.1,
        ["dislike"] = -0.5,
    };

    /// <summary>
    /// The weighted sum of a card's acts. <paramref name="categoryEvents"/> narrows which positive acts count (a category lists the events that are
    /// yield for it; null or empty means all); a dislike always counts.
    /// </summary>
    public static double Score(IEnumerable<string> acts, IReadOnlyCollection<string>? categoryEvents) =>
        acts.Distinct(StringComparer.Ordinal).Where(a => Weights.ContainsKey(a) && (a == "dislike" || categoryEvents is not { Count: > 0 } || categoryEvents.Contains(a)))
            .Sum(a => Weights[a]);

    public static bool Positive(IEnumerable<string> acts, IReadOnlyCollection<string>? categoryEvents) => Score(acts, categoryEvents) >= PositiveAt;
}

/// <summary>Trains the yield model from the log of what was done with the cards shown.</summary>
public sealed class SwipeYield(FeedDb db, CategoryStore categories, IOptions<FeedEaterOptions> options)
{
    public sealed record Status(int Positives, int Negatives, int Needed, double? CentroidAuc, double? LearnedAuc)
    {
        public bool Ready => Positives >= Needed && Negatives >= Needed;
    }

    private sealed record Row
    {
        public float[] Embedding { get; init; } = [];
        public string[] Acts { get; init; } = [];
        public string? Category { get; init; }
    }

    public async Task<IReadOnlyList<LabeledVector>> LabeledAsync(CancellationToken ct)
    {
        var narrowing = (await categories.AllAsync(ct)).Where(c => c.Events.Length > 0).ToDictionary(c => c.Key, c => (IReadOnlyCollection<string>)c.Events, StringComparer.Ordinal);
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Row>(new CommandDefinition(
            """
            select i.embedding::real[] as embedding, e.acts, e.category
            from swipe_events e join items i on i.id = e.ref_id
            where i.embedding is not null and e.kind = 'item'
            union all
            select i.embedding::real[], array['idea'], s.category
            from post_seeds s join items i on i.id = s.item_id where s.status = 'sent' and i.embedding is not null
            union all
            select i.embedding::real[], array['recall'], c.category
            from recall_reviews r join recall_cards c on c.id = r.card_id join items i on i.id = c.item_id
            where r.result = 'good' and r.step_before >= 1 and i.embedding is not null
            """, cancellationToken: ct));
        return rows.Select(r => new LabeledVector(
            r.Embedding, YieldLabels.Positive(r.Acts, r.Category is not null && narrowing.TryGetValue(r.Category, out var events) ? events : null))).ToList();
    }

    /// <summary>The model, once there are enough positive and enough negative examples; null before that (the ranking then runs without it).</summary>
    public async Task<LogisticModel?> ModelAsync(CancellationToken ct)
    {
        var data = await LabeledAsync(ct);
        var needed = options.Value.Swipe.YieldMinEvents;
        return data.Count(d => d.Liked) >= needed && data.Count(d => !d.Liked) >= needed ? LogisticModel.Train(data) : null;
    }

    public async Task<Status> StatusAsync(CancellationToken ct)
    {
        var data = await LabeledAsync(ct);
        var needed = options.Value.Swipe.YieldMinEvents;
        var status = new Status(data.Count(d => d.Liked), data.Count(d => !d.Liked), needed, null, null);
        if (!status.Ready)
        {
            return status;
        }

        var (centroid, learned) = LearnedTaste.CrossValidate(data);
        return status with { CentroidAuc = centroid, LearnedAuc = learned };
    }
}
