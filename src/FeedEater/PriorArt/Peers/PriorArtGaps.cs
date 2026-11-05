using System.Text.Json;

namespace FeedEater.PriorArt.Peers;

public sealed record GapEvidence(string? Repo, string? Commit, string? Path, string? Lines, string? Url);

/// <summary>One peer's best fact for a feature.</summary>
public sealed record PeerFeature(string Tool, string? ToolRef, Guid FactId, string Text, double Confidence, GapEvidence Evidence);

public sealed record ApproachSummary(string Approach, IReadOnlyList<string> Tools);

public sealed record GapRow(string Feature, string Slug, IReadOnlyList<PeerFeature> Peers, IReadOnlyList<ApproachSummary> Approaches);

public sealed record OwnOnlyRow(string Feature, string Slug, string Text, double Confidence, GapEvidence Evidence);

public sealed record PeerGaps(string Peer, string? PeerRef, IReadOnlyList<string> Missing);

public sealed record GapReport(
    string Repo, DateTimeOffset KnownAt, int MinPeers, string PeerScope, IReadOnlyList<string> Peers,
    IReadOnlyList<GapRow> Gaps, IReadOnlyList<OwnOnlyRow> OwnOnly, IReadOnlyList<ApproachSummary> SharedApproaches, IReadOnlyList<PeerGaps> ByPeer);

/// <summary>
/// Feature gaps: what at least <c>min_peers</c> peers have and the own repo lacks. Pure over the facts the base held at one time, so a
/// fixture can check it; marketing-labelled facts (a README's boast) count for nothing.
/// </summary>
public static class GapCompute
{
    private sealed record Ranked(PeerFeature Peer, Guid ToolId);

    public static GapReport Compute(
        string repo, DateTimeOffset knownAt, int minPeers, string peerScope, IReadOnlySet<string>? peerRefs,
        IReadOnlyList<FeatureFactRow> facts, IReadOnlyList<FeatureEntity> entities, IReadOnlyList<ApproachUse> uses)
    {
        var byId = entities.ToDictionary(e => e.Id);
        var ownRef = "gh:" + repo;
        var counted = facts.Where(f => !IsMarketing(f.Locator))
            .Select(f => (Fact: f, Feature: FeatureResolver.Canonical(byId, f.FeatureId)))
            .Where(x => byId.ContainsKey(x.Feature)).ToList();

        bool IsOwn(string? toolRef) => string.Equals(toolRef, ownRef, StringComparison.OrdinalIgnoreCase);
        bool IsPeer(string? toolRef) => !IsOwn(toolRef) && (peerRefs is null || (toolRef is not null && peerRefs.Contains(toolRef)));

        var own = counted.Where(x => IsOwn(x.Fact.ToolRef)).GroupBy(x => x.Feature).ToDictionary(g => g.Key, g => Best(g.Select(x => x.Fact)));
        var peerFacts = counted.Where(x => IsPeer(x.Fact.ToolRef)).ToList();
        var peerUses = uses.Where(u => IsPeer(u.ToolRef)).ToList();

        var perFeature = peerFacts.GroupBy(x => x.Feature)
            .Select(g => (Feature: g.Key, Peers: g.GroupBy(x => x.Fact.ToolId).Select(t => Best(t.Select(x => x.Fact))).OrderBy(p => p.Peer.Tool, StringComparer.Ordinal).ToList()))
            .ToList();

        var gapFeatures = perFeature
            .Where(f => f.Peers.Count >= minPeers && !own.ContainsKey(f.Feature))
            .Select(f =>
            {
                var peerIds = f.Peers.Select(p => p.ToolId).ToHashSet();
                var approaches = peerUses.Where(u => peerIds.Contains(u.ToolId)).GroupBy(u => u.ApproachName)
                    .Select(g => new ApproachSummary(g.Key, g.Select(u => u.ToolName).Distinct().Order(StringComparer.Ordinal).ToList()))
                    .OrderByDescending(a => a.Tools.Count).ThenBy(a => a.Approach, StringComparer.Ordinal).Take(5).ToList();
                return (f.Feature, Row: new GapRow(byId[f.Feature].Name, SlugOf(byId[f.Feature]), f.Peers.Select(p => p.Peer).ToList(), approaches));
            })
            .OrderByDescending(g => g.Row.Peers.Count).ThenByDescending(g => g.Row.Peers.Average(p => p.Confidence)).ThenBy(g => g.Row.Feature, StringComparer.Ordinal)
            .ToList();

        var peerHas = perFeature.Select(f => f.Feature).ToHashSet();
        var ownOnly = own.Where(o => !peerHas.Contains(o.Key))
            .Select(o => new OwnOnlyRow(byId[o.Key].Name, SlugOf(byId[o.Key]), o.Value.Peer.Text, o.Value.Peer.Confidence, o.Value.Peer.Evidence))
            .OrderBy(o => o.Feature, StringComparer.Ordinal).ToList();

        var shared = peerUses.GroupBy(u => u.ApproachName)
            .Select(g => new ApproachSummary(g.Key, g.Select(u => u.ToolName).Distinct().Order(StringComparer.Ordinal).ToList()))
            .Where(a => a.Tools.Count >= minPeers).OrderByDescending(a => a.Tools.Count).ThenBy(a => a.Approach, StringComparer.Ordinal).ToList();

        var gapIds = gapFeatures.Select(g => g.Feature).ToHashSet();
        var byPeer = peerFacts.GroupBy(x => x.Fact.ToolId).Select(g =>
            {
                var has = g.Select(x => x.Feature).ToHashSet();
                var first = g.First().Fact;
                return new PeerGaps(first.ToolName, first.ToolRef, gapFeatures.Where(f => has.Contains(f.Feature)).Select(f => f.Row.Feature).ToList());
            })
            .OrderBy(p => p.Peer, StringComparer.Ordinal).ToList();

        var peers = byPeer.Select(p => p.Peer).ToList();
        return new GapReport(repo, knownAt, minPeers, peerScope, peers, gapFeatures.Select(g => g.Row).ToList(), ownOnly, shared, byPeer);
    }

    private static Ranked Best(IEnumerable<FeatureFactRow> facts)
    {
        var best = facts.OrderByDescending(f => ConfidenceOf(f.Locator)).ThenBy(f => f.FactId).First();
        return new Ranked(new PeerFeature(best.ToolName, best.ToolRef, best.FactId, best.Text, ConfidenceOf(best.Locator), EvidenceOf(best.Locator)), best.ToolId);
    }

    private static string SlugOf(FeatureEntity e) => e.ExternalRef is { } r && r.StartsWith("feature:", StringComparison.Ordinal) ? r["feature:".Length..] : PriorArtText.Slug(e.Name);

    private static bool IsMarketing(string locatorJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(locatorJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() == "marketing";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double ConfidenceOf(string locatorJson) => PriorArtStore.ConfidenceOf(locatorJson) ?? 0;

    private static GapEvidence EvidenceOf(string locatorJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(locatorJson);
            var o = doc.RootElement;
            string? Get(string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new GapEvidence(Get("repo"), Get("commit"), Get("path"), Get("lines"), Get("url"));
        }
        catch (JsonException)
        {
            return new GapEvidence(null, null, null, null, null);
        }
    }
}

/// <summary>Reads the facts at one time and hands them to <see cref="GapCompute"/>. Without <c>peers</c> the peer set file narrows the peers when one is configured.</summary>
public sealed class PriorArtGaps(PriorArtStore store, PeerSetClient peerSet, TimeProvider time)
{
    public async Task<GapReport> QueryAsync(string repo, IReadOnlyList<string>? peers, int minPeers, DateTimeOffset? knownAt, CancellationToken ct)
    {
        if (!repo.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{repo}' is not owner/name.", nameof(repo));
        }

        if (minPeers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minPeers), "min_peers is at least 1.");
        }

        await store.EnsureReadyAsync(ct);
        var at = knownAt ?? time.GetUtcNow();
        HashSet<string>? refs = null;
        var scope = "every other tool with a has_feature fact";
        if (peers is { Count: > 0 })
        {
            refs = peers.Select(AsRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
            scope = "the peers argument";
        }
        else if (await SetPeersAsync(repo, ct) is { } fromSet)
        {
            refs = fromSet.Select(AsRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
            scope = "the peer set file";
        }

        var facts = await store.FeatureFactsAsync(at, ct);
        var entities = await store.FeatureEntitiesAsync(ct);
        var uses = await store.UsesAtAsync(at, ct);
        return GapCompute.Compute(repo, at, minPeers, scope, refs, facts, entities, uses);
    }

    private async Task<IReadOnlyList<string>?> SetPeersAsync(string repo, CancellationToken ct)
    {
        try
        {
            var text = await peerSet.TextAsync(ct);
            var set = text is null ? null : PeerSetParser.Parse(text);
            return set is not null && string.Equals(set.Repo, repo, StringComparison.OrdinalIgnoreCase) ? set.Peers : null;
        }
        catch (Exception e) when (e is HttpRequestException or PeerSetFormatException)
        {
            return null;   // an unreachable or broken set file widens the answer to every tool; the scope field says so
        }
    }

    private static string AsRef(string peer) => peer.StartsWith("gh:", StringComparison.OrdinalIgnoreCase) ? peer : "gh:" + peer.Trim();
}
