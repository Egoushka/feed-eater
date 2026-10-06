using System.Text.Json.Serialization;

namespace FeedEater.Hype;

public sealed record VerdictCounts(int Grew, int Alive, int Quiet, int Gone)
{
    [JsonIgnore]
    public int Total => Grew + Alive + Quiet + Gone;

    /// <summary>Grew or alive: the project was still going.</summary>
    [JsonIgnore]
    public int Lasted => Grew + Alive;

    public static VerdictCounts Of(IEnumerable<RepoVerdict> verdicts)
    {
        var all = verdicts.ToList();
        return new VerdictCounts(
            all.Count(v => v == RepoVerdict.Grew), all.Count(v => v == RepoVerdict.Alive),
            all.Count(v => v == RepoVerdict.Quiet), all.Count(v => v == RepoVerdict.Gone));
    }
}

/// <summary>One scored 👍 item: what it was, what the repo did, and what else happened around it.</summary>
public sealed record AutopsyItem(
    long ItemId, string Title, string Url, string Feed, string Repo, int? Relevance, RepoVerdict Verdict, int StarsThen, int? StarsNow,
    double? StarsGrowthPercent, bool PushedRecently, bool NewRelease, IdeaOutcome Idea, bool Saved, int RelatedLater);

public sealed record AutopsyGroup(string Label, VerdictCounts Counts);

/// <summary>
/// <see cref="NoRepo"/>: 👍 items that link no GitHub repo. <see cref="NoBaseline"/>: the repo was already gone when first looked up.
/// <see cref="Unchecked"/>: GitHub refused or failed this month; they are tried again next month. None of the three is scored.
/// </summary>
public sealed record AutopsyReport(
    DateTimeOffset At, VerdictCounts Total, int NoRepo, int NoBaseline, int Unchecked,
    IReadOnlyList<AutopsyGroup> ByRelevance, IReadOnlyList<AutopsyGroup> ByFeed, IReadOnlyList<AutopsyItem> Items)
{
    public const string NoRelevance = "none";

    [JsonIgnore]
    public int Counted => Items.Count + NoRepo + NoBaseline + Unchecked;
}

public static class AutopsyScorer
{
    /// <summary>Totals and the two splits. Relevance groups run high to low, feeds by how many of their items lasted, then by size.</summary>
    public static AutopsyReport Build(IReadOnlyList<AutopsyItem> items, int noRepo, int noBaseline, int notChecked, DateTimeOffset at) => new(
        at, VerdictCounts.Of(items.Select(i => i.Verdict)), noRepo, noBaseline, notChecked,
        items.GroupBy(i => i.Relevance).OrderByDescending(g => g.Key ?? -1)
            .Select(g => new AutopsyGroup(g.Key?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? AutopsyReport.NoRelevance, VerdictCounts.Of(g.Select(i => i.Verdict)))).ToList(),
        items.GroupBy(i => i.Feed).Select(g => new AutopsyGroup(g.Key, VerdictCounts.Of(g.Select(i => i.Verdict))))
            .OrderByDescending(g => (double)g.Counts.Lasted / g.Counts.Total).ThenByDescending(g => g.Counts.Total).ThenBy(g => g.Label, StringComparer.Ordinal).ToList(),
        items.OrderBy(i => i.Verdict).ThenBy(i => i.ItemId).ToList());
}
