using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Llm;

namespace FeedEater.PriorArt;

public sealed record StaleFlag(string Reason);

public sealed record FactSourceView(string Kind, string Ref, string Trust, JsonNode? Locator, string? Quote);

public sealed record PriorArtFact(
    Guid Id, string Predicate, string Text, string Tool, string? ToolRef, string? Approach, double Score, double? Similarity, bool Relevant,
    double Confidence, string Trust, StaleFlag? Stale, DateTime? ValidFrom, DateTime RecordedAt, IReadOnlyList<FactSourceView> Sources);

public sealed record Pattern(string Approach, IReadOnlyList<string> Tools, IReadOnlyList<Guid> FactIds);

/// <param name="Coverage">hit, thin or miss.</param>
/// <param name="Ranking">Which lists ranked the facts: words and meaning, or words only when the embedder was down.</param>
/// <param name="Research">On thin and miss: a request/v1 to send to chargehand for a repository you name, and how to store its result.</param>
public sealed record PriorArtAnswer(
    string Question, DateTimeOffset? KnownAt, string Coverage, string Reason, string Ranking, IReadOnlyList<PriorArtFact> Facts, IReadOnlyList<Pattern> Patterns, JsonNode? Research);

public sealed class PriorArtSearch(PriorArtStore store, IPriorArtEmbedder embedder, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    /// <summary>How long a search waits for the question's embedding before it falls back to words alone.</summary>
    internal static TimeSpan EmbedTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<PriorArtAnswer> SearchAsync(string question, DateTimeOffset? knownAt, int limit, CancellationToken ct)
    {
        var o = options.Value.PriorArt;
        var now = time.GetUtcNow();
        if (knownAt > now)
        {
            throw new ArgumentOutOfRangeException(nameof(knownAt), "known_at is in the future: nothing is known yet.");
        }

        await store.EnsureReadyAsync(ct);
        float[]? vector = null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(EmbedTimeout);
            try
            {
                vector = (await embedder.EmbedAsync([question], timeout.Token))[0];
            }
            catch (Exception ex) when (ex is HttpRequestException or BudgetExceededException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Words alone still answer.
            }
        }

        var at = knownAt ?? now;
        var hits = await store.Facts.SearchAsync(question, vector ?? [], vector is null ? null : embedder.Model, limit, at, knownAt, ct);
        var ids = hits.Select(h => h.Fact.Id).ToArray();
        var details = (await store.DetailsAsync(ids, ct)).ToDictionary(d => d.Id);
        var sources = (await store.SourcesAsync(ids, ct)).ToLookup(s => s.FactId);
        var inactive = await store.InactiveToolsAsync(details.Values.Where(d => d.SubjectKind == PriorArtSchema.EntityTool).Select(d => d.SubjectId).Distinct().ToArray(), at, ct);
        var staleAfter = TimeSpan.FromDays(o.StaleAfterDays);

        var facts = new List<PriorArtFact>();
        foreach (var hit in hits)
        {
            if (!details.TryGetValue(hit.Fact.Id, out var d))
            {
                continue;
            }

            var views = sources[d.Id].Select(s => new FactSourceView(s.SourceKind, s.SourceRef, s.Trust, ParseLocator(s.Locator), s.Quote)).ToList();
            var confidence = sources[d.Id].Select(s => PriorArtStore.ConfidenceOf(s.Locator)).Where(c => c is not null).Select(c => c!.Value).DefaultIfEmpty(0.5).Max();
            var trust = sources[d.Id].Select(s => s.Trust).Distinct().Order().LastOrDefault() ?? "agent";
            var recorded = DateTime.SpecifyKind(d.RecordedAt, DateTimeKind.Utc);
            StaleFlag? stale = null;
            if (d.SubjectKind == PriorArtSchema.EntityTool && inactive.Contains(d.SubjectId))
            {
                stale = new StaleFlag("tool_not_active");
            }
            else if (!PriorArtSchema.NotEmbedded.Contains(d.Predicate) && now - recorded > staleAfter)
            {
                stale = new StaleFlag($"recorded_over_{o.StaleAfterDays}_days_ago");
            }

            var relevant = hit.Similarity >= o.MinSimilarity;
            var approach = d.Predicate == "uses" ? d.ObjectName : d.Predicate == "approach_for" ? d.SubjectName : null;
            facts.Add(new PriorArtFact(
                d.Id, d.Predicate, d.Text, d.SubjectKind == PriorArtSchema.EntityTool ? d.SubjectName : d.SubjectName, d.SubjectRef, approach, hit.Score, hit.Similarity,
                relevant, Math.Round(confidence, 3), TrustWord(trust), stale, d.ValidFrom is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null, recorded, views));
        }

        var relevantFacts = facts.Where(f => f.Relevant).ToList();
        var tools = relevantFacts.Where(f => f.ToolRef is not null && f.Predicate != "approach_for").Select(f => f.ToolRef).Distinct().Count();
        var (coverage, reason) = relevantFacts.Count >= o.HitMinFacts && tools >= o.HitMinTools
            ? ("hit", $"{relevantFacts.Count} relevant facts from {tools} tools")
            : relevantFacts.Count > 0
                ? ("thin", $"{relevantFacts.Count} relevant facts from {tools} tools; a hit needs {o.HitMinFacts} from {o.HitMinTools}")
                : ("miss", facts.Count == 0
                    ? "the base holds nothing that matches"
                    : vector is null
                        ? "the embedder did not answer; a word match alone does not make a fact relevant"
                        : "nothing reaches the similarity threshold");
        return new PriorArtAnswer(
            question, knownAt, coverage, reason, vector is null ? "words only (the embedder did not answer)" : "words and meaning", facts,
            await PatternsAsync(details.Values, ct), coverage == "hit" ? null : ResearchRequest(question));
    }

    /// <summary>Approaches that at least two tools use, found among the answer's approaches: computed on every read, never stored.</summary>
    private async Task<IReadOnlyList<Pattern>> PatternsAsync(IEnumerable<FactDetail> details, CancellationToken ct)
    {
        var approaches = details.SelectMany(d => new[] { d.Predicate == "uses" ? d.ObjectId : null, d.Predicate == "approach_for" ? d.SubjectId : null })
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        if (approaches.Length == 0)
        {
            return [];
        }

        return (await store.UsesAsync(approaches, ct)).GroupBy(u => u.ApproachName)
            .Where(g => g.Select(u => u.ToolId).Distinct().Count() >= 2)
            .Select(g => new Pattern(g.Key, g.Select(u => u.ToolName).Distinct().ToList(), g.Select(u => u.FactId).ToList()))
            .ToList();
    }

    /// <summary>The call that fills a gap: chargehand reads the repository you name (already cloned under its repository roots) and the result goes to <c>prior_art_ingest</c>.</summary>
    internal static JsonNode ResearchRequest(string question)
    {
        var problem = PriorArtText.Slug(string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(8)));
        return new JsonObject
        {
            ["note"] = "Name a repository that is checked out under chargehand's repository roots in place of OWNER/NAME, send this request to chargehand, then pass the result/v1 reply to prior_art_ingest.",
            ["request"] = new JsonObject
            {
                ["contract_version"] = "request/v1",
                ["text"] = "Read this repository to answer: " + question + "\n\n" +
                    "Return one claim per distinct finding, each with file evidence (path and lines). Start every claim with a tag: " +
                    "[uses:<approach-slug>] for how the repository solves part of the problem, [limitation] for what it does not do or gets wrong, " +
                    "[addresses] for a problem it targets. Use lower-case kebab-case approach slugs. Do not claim anything the code does not show.",
                ["context"] = new JsonObject
                {
                    ["interactive"] = false,
                    ["preset"] = "cheap",
                    ["repository"] = new JsonObject { ["path"] = "OWNER/NAME" },
                },
            },
            ["ingest_with"] = new JsonObject { ["tool"] = "OWNER/NAME", ["problem"] = problem, ["format"] = "result_v1" },
        };
    }

    private static JsonNode? ParseLocator(string locator)
    {
        try
        {
            return JsonNode.Parse(locator);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TrustWord(string trust) => trust switch { "third_party" => "third_party", "owner" => "owner", _ => "agent" };
}
