using System.Collections.Frozen;
using Switchknife.Facts;

namespace FeedEater.PriorArt;

/// <summary>What the prior-art namespace of <c>facts</c> holds. The spec is <c>context/ai-setup/drafts/prior-art-base-spec.md</c>.</summary>
public static class PriorArtSchema
{
    public const string Namespace = "prior-art";

    public const string Statement = "statement";

    /// <summary>One package a tool depends on; the object is a <see cref="EntityPackage"/> entity, the version is kept in the fact's text.</summary>
    public const string DependsOn = "depends_on";

    /// <summary>A feature a README card found; the object is a <see cref="EntityFeature"/> entity.</summary>
    public const string HasFeature = "has_feature";

    /// <summary>GitHub's repo size (KB), from the same call the existing snapshot facts come from.</summary>
    public const string DiskUsage = "disk_usage";

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
        (DependsOn, Cardinality.Many),
        (HasFeature, Cardinality.Many),
        (DiskUsage, Cardinality.One),
    ];

    /// <summary>Counters read from GitHub: they carry no meaning to search, so they are not embedded.</summary>
    public static readonly FrozenSet<string> NotEmbedded =
        new[] { "status", "stars", "last_push", "latest_release", "license", DependsOn, DiskUsage }.ToFrozenSet(StringComparer.Ordinal);

    public const string EntityTool = "tool";
    public const string EntityProblem = "problem";
    public const string EntityApproach = "approach";
    public const string EntityPackage = "package";
    public const string EntityFeature = "feature";

    public const string SourceRepoRead = "repo-read";
    public const string SourceAudit = "audit";
    public const string SourceWebNote = "web-note";
    public const string SourceSnapshot = "snapshot";

    /// <summary>A repo's dependency graph and GitHub counters, read with no model.</summary>
    public const string SourceFingerprint = "fingerprint";

    /// <summary>A README card (addresses, has_feature, uses), written by one model call.</summary>
    public const string SourceCard = "card";

    /// <summary>
    /// A worker's reading of code and our own audits are an agent's word; a README, a web page or a peer's own dependency
    /// graph and card are a stranger's, so a boast never retires what a worker read in the code.
    /// </summary>
    public static EpisodeTrust TrustOf(string sourceKind) => sourceKind switch
    {
        SourceWebNote or SourceFingerprint or SourceCard => EpisodeTrust.ThirdParty,
        _ => EpisodeTrust.Agent,
    };
}
