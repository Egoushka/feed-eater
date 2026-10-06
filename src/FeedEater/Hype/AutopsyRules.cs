using System.Text.Json.Serialization;
using FeedEater.Signals;

namespace FeedEater.Hype;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RepoVerdict { Grew, Alive, Quiet, Gone }

/// <summary>
/// What became of the idea filed for an item. The shipped-it work adds Shipped, Dropped and Open here and fills them from
/// <c>AutopsyStore.IdeaOutcomeSql</c>; until then an idea is only filed or not.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdeaOutcome { None, Filed }

/// <summary>A repo as it was when its item got a 👍. A null <see cref="Repo"/> means the item links none; a null <see cref="Stars"/> that the repo was already gone.</summary>
public sealed record RepoSnapshot(
    long ItemId, string? Repo, DateTimeOffset TakenAt, int? Stars, DateTimeOffset? PushedAt, string? ReleaseTag, DateTimeOffset? CreatedAt);

public sealed record Judgement(
    RepoVerdict Verdict, double? StarsGrowthPercent, int? StarsNow, bool PushedRecently, bool NewRelease, IdeaOutcome Idea, bool Saved, int RelatedLater);

/// <summary>The verdict on what a repo did after its item got a 👍. Pure: the job fetches the facts, this decides.</summary>
public static class AutopsyRules
{
    /// <summary>Stars up by this many percent or more since the snapshot: grew. A snapshot of 0 stars counts as 1.</summary>
    public const int GrewStarsPercent = 20;

    /// <summary>Last push within this many days of the autopsy: alive.</summary>
    public const int AliveDays = 30;

    /// <summary>A snapshot is scored once it is this old; the job runs monthly, so it takes every snapshot that has reached the age and is not scored yet.</summary>
    public const int MinAgeDays = 87;

    /// <summary>A snapshot still unscored at this age (GitHub kept refusing it) is dropped from the autopsy.</summary>
    public const int MaxAgeDays = 180;

    public static string Legend =>
        $"Verdict, first match wins: gone = the repo answers 404 or is archived; grew = stars up {GrewStarsPercent}% or more since the snapshot; " +
        $"alive = last push within {AliveDays} days; quiet = none of those. A snapshot is scored once it is {MinAgeDays} days old.";

    /// <param name="now">The facts today; null when the repo is gone (404).</param>
    /// <param name="at">When the autopsy runs.</param>
    public static Judgement Judge(RepoSnapshot snapshot, RepoFacts? now, IdeaOutcome idea, bool saved, int relatedLater, DateTimeOffset at)
    {
        var growth = snapshot.Stars is { } before && now is not null ? (now.Stars - before) * 100.0 / Math.Max(before, 1) : (double?)null;
        var pushedRecently = now?.PushedAt is { } pushed && at - pushed <= TimeSpan.FromDays(AliveDays);
        var newRelease = now?.ReleaseTag is { } tag && tag != snapshot.ReleaseTag;
        var verdict = now is null or { Archived: true } ? RepoVerdict.Gone
            : snapshot.Stars is { } then && (now.Stars - then) * 100 >= GrewStarsPercent * Math.Max(then, 1) ? RepoVerdict.Grew
            : pushedRecently ? RepoVerdict.Alive
            : RepoVerdict.Quiet;
        return new Judgement(verdict, growth, now?.Stars, pushedRecently, newRelease, idea, saved, relatedLater);
    }
}
