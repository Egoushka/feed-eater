using System.Collections.Frozen;
using Switchknife.Facts;

namespace FeedEater.PriorArt;

/// <summary>What the prior-art namespace of <c>facts</c> holds. The spec is <c>context/ai-setup/drafts/prior-art-base-spec.md</c>.</summary>
public static class PriorArtSchema
{
    public const string Namespace = "prior-art";

    public const string Statement = "statement";

    public static readonly IReadOnlyList<(string Name, Cardinality Cardinality)> Predicates =
    [
        ("addresses", Cardinality.Many),
        ("uses", Cardinality.Many),
        ("approach_for", Cardinality.Many),
        ("limitation", Cardinality.Many),
        ("reports_result", Cardinality.Event),
        ("status", Cardinality.One),
        ("stars", Cardinality.One),
        ("last_push", Cardinality.One),
        ("latest_release", Cardinality.One),
        ("license", Cardinality.One),
        ("assessed", Cardinality.One),
        (Statement, Cardinality.Many),
    ];

    /// <summary>Counters read from GitHub: they carry no meaning to search, so they are not embedded.</summary>
    public static readonly FrozenSet<string> NotEmbedded = new[] { "status", "stars", "last_push", "latest_release", "license" }.ToFrozenSet(StringComparer.Ordinal);

    public const string EntityTool = "tool";
    public const string EntityProblem = "problem";
    public const string EntityApproach = "approach";

    public const string SourceRepoRead = "repo-read";
    public const string SourceAudit = "audit";
    public const string SourceWebNote = "web-note";
    public const string SourceSnapshot = "snapshot";

    /// <summary>
    /// A worker's reading of code and our own audits are an agent's word; a README or web page is a stranger's, so a boast never
    /// retires what a worker read in the code.
    /// </summary>
    public static EpisodeTrust TrustOf(string sourceKind) => sourceKind switch
    {
        SourceWebNote => EpisodeTrust.ThirdParty,
        _ => EpisodeTrust.Agent,
    };
}
