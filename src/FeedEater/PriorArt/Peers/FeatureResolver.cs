using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Switchknife.Facts;
using FeedEater.Llm;

namespace FeedEater.PriorArt.Peers;

/// <summary>The cheap call that decides whether two feature names are one feature; a seam so tests need no gateway.</summary>
public interface IFeatureJudge
{
    Task<bool> SameAsync(string a, string b, CancellationToken ct);
}

public sealed partial class LiteLlmFeatureJudge(LiteLlmClient llm, IOptions<FeedEaterOptions> options) : IFeatureJudge
{
    private const string System =
        "You compare two names of software features. Answer with JSON only: {\"same\": true} when both names describe the same capability " +
        "a user or operator gets (one is a rewording or a narrower name of the other), else {\"same\": false}. A related but different capability is false.";

    [GeneratedRegex("\"same\"\\s*:\\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex SameTrue();

    public async Task<bool> SameAsync(string a, string b, CancellationToken ct)
    {
        var reply = await llm.ChatAsync(options.Value.PeerHarvest.CardModel, System, $"A: {a}\nB: {b}", 30, "peer-harvest-merge", ct);
        return SameTrue().IsMatch(reply.Content);
    }
}

public sealed record MergeOutcome(int Compared, int Merged, int Rejected, int Deferred)
{
    public static readonly MergeOutcome None = new(0, 0, 0, 0);
}

/// <summary>
/// After a repo is read, matches its feature names against the other tools': close names (embedding cosine) go to one cheap
/// confirm-or-reject call. A confirmed pair becomes <c>merged_into</c> plus an alias plus a <c>same_as</c> fact; a rejected one a
/// <c>distinct_from</c> fact, so neither is asked twice. Nothing is deleted, so a merge can be listed and undone.
/// </summary>
public sealed class FeatureResolver(PriorArtStore store, IPriorArtEmbedder embedder, IFeatureJudge judge, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    private const int Batch = 64;

    public async Task<MergeOutcome> ResolveAsync(string repo, CancellationToken ct)
    {
        await store.EnsureReadyAsync(ct);
        var settings = options.Value.PeerHarvest;
        var now = time.GetUtcNow();
        var entities = (await store.FeatureEntitiesAsync(ct)).ToDictionary(e => e.Id);
        var facts = await store.FeatureFactsAsync(now, ct);
        var ownRef = "gh:" + repo;
        var mine = facts.Where(f => Same(f.ToolRef, ownRef)).Select(f => Canonical(entities, f.FeatureId)).ToHashSet();
        var others = facts.Where(f => !Same(f.ToolRef, ownRef)).Select(f => Canonical(entities, f.FeatureId)).ToHashSet();
        others.ExceptWith(mine);
        if (mine.Count == 0 || others.Count == 0)
        {
            return MergeOutcome.None;
        }

        var ids = mine.Concat(others).ToList();
        var vectors = new Dictionary<Guid, float[]>();
        for (var i = 0; i < ids.Count; i += Batch)
        {
            var chunk = ids.Skip(i).Take(Batch).ToList();
            var embedded = await embedder.EmbedAsync(chunk.Select(id => entities[id].Name).ToList(), ct);
            for (var j = 0; j < chunk.Count; j++)
            {
                vectors[chunk[j]] = embedded[j];
            }
        }

        var decided = await store.DecidedPairsAsync(ct);
        var candidates = (from m in mine
                          from o in others
                          let score = Cosine(vectors[m], vectors[o])
                          where score >= settings.FeatureMergeSimilarity && !decided.Contains((m, o))
                          orderby score descending, entities[m].Name, entities[o].Name
                          select (A: m, B: o, Score: score)).ToList();
        var counts = facts.GroupBy(f => Canonical(entities, f.FeatureId)).ToDictionary(g => g.Key, g => g.Select(f => f.ToolId).Distinct().Count());
        int compared = 0, merged = 0, rejected = 0, joined = 0;
        foreach (var (a0, b0, score) in candidates.Take(settings.MaxMergeChecks))
        {
            var a = Canonical(entities, a0);
            var b = Canonical(entities, b0);
            if (a == b || decided.Contains((a, b)))
            {
                joined++;   // an earlier merge in this run already joined them
                continue;
            }

            bool same;
            try
            {
                same = await judge.SameAsync(entities[a].Name, entities[b].Name, ct);
            }
            catch (Exception e) when (e is HttpRequestException or BudgetExceededException)
            {
                break;
            }

            compared++;
            if (same)
            {
                var (survivor, loser) = Order(entities, counts, a, b);
                await store.SetMergedAsync(loser, survivor, entities[loser].Name, ct);
                entities[loser] = entities[loser] with { MergedInto = survivor };
                await RecordAsync(PriorArtSchema.SameAs, entities[loser], entities[survivor], score, $"{entities[loser].ExternalRef}>{entities[survivor].ExternalRef}", $"{entities[loser].Name} is the same feature as {entities[survivor].Name}.", ct);
                merged++;
            }
            else
            {
                await RecordAsync(PriorArtSchema.DistinctFrom, entities[a], entities[b], score, Pair(entities[a], entities[b]), $"{entities[a].Name} and {entities[b].Name} are different features.", ct);
                rejected++;
            }

            decided.Add((a, b));
            decided.Add((b, a));
        }

        return new MergeOutcome(compared, merged, rejected, candidates.Count - compared - joined);
    }

    /// <summary>Reverses one merge by its <c>same_as</c> fact id and records the pair as different so it is not merged again. False when no live merge has that id.</summary>
    public async Task<bool> UndoAsync(Guid factId, CancellationToken ct)
    {
        await store.EnsureReadyAsync(ct);
        var merge = (await store.MergesAsync(ct)).FirstOrDefault(m => m.FactId == factId);
        if (merge is null)
        {
            return false;
        }

        await store.Facts.RetractAsync(factId, ct);
        await store.ClearMergedAsync(merge.LoserId, merge.SurvivorId, merge.LoserName, ct);
        var entities = (await store.FeatureEntitiesAsync(ct)).ToDictionary(e => e.Id);
        await RecordAsync(
            PriorArtSchema.DistinctFrom, entities[merge.LoserId], entities[merge.SurvivorId], 1, "undo:" + Pair(entities[merge.LoserId], entities[merge.SurvivorId]),
            $"{merge.LoserName} and {merge.SurvivorName} are different features (a merge was undone).", ct);
        return true;
    }

    private async Task RecordAsync(string predicate, FeatureEntity subject, FeatureEntity target, double score, string sourceRef, string text, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var locator = JsonSerializer.Serialize(new Dictionary<string, object> { ["similarity"] = Math.Round(score, 4), ["confidence"] = 1.0 });
        var episode = new NewEpisode(PriorArtSchema.SourceFeatureMerge, sourceRef, PriorArtText.Sha256(sourceRef + "|" + predicate), now, PriorArtSchema.TrustOf(PriorArtSchema.SourceFeatureMerge));
        await store.Facts.IngestAsync(episode, [new NewFact(subject.Id, predicate, null, now, Text: PriorArtText.Cut(text), LocatorJson: locator, ObjectEntity: target.Id)], ct);
    }

    private static string Pair(FeatureEntity a, FeatureEntity b) =>
        string.CompareOrdinal(a.ExternalRef, b.ExternalRef) <= 0 ? $"{a.ExternalRef}|{b.ExternalRef}" : $"{b.ExternalRef}|{a.ExternalRef}";

    /// <summary>The feature more tools have survives; a tie goes to the name that sorts first, so a rerun picks the same one.</summary>
    private static (Guid Survivor, Guid Loser) Order(Dictionary<Guid, FeatureEntity> entities, Dictionary<Guid, int> counts, Guid a, Guid b)
    {
        var ca = counts.GetValueOrDefault(a);
        var cb = counts.GetValueOrDefault(b);
        var first = ca != cb ? ca > cb : string.CompareOrdinal(entities[a].ExternalRef, entities[b].ExternalRef) <= 0;
        return first ? (a, b) : (b, a);
    }

    internal static Guid Canonical(IReadOnlyDictionary<Guid, FeatureEntity> entities, Guid id)
    {
        for (var hops = 0; hops < 16 && entities.TryGetValue(id, out var e) && e.MergedInto is { } next; hops++)
        {
            id = next;
        }

        return id;
    }

    private static bool Same(string? toolRef, string expected) => string.Equals(toolRef, expected, StringComparison.OrdinalIgnoreCase);

    internal static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
