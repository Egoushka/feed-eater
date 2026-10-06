using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FeedEater.Eval;

public sealed record EvalRow(
    long Id, string Title, int Vote, int? OldRelevance, string? OldKind, string? OldSummary,
    int NewRelevance, string NewKind, string? NewSummary, string Text);

/// <summary>The markdown report: how well the current prompts' relevance agrees with the votes, and how often "new" is claimed without evidence.</summary>
public static partial class EvalReport
{
    public const int MinUp = 10;
    public const int MinDown = 10;

    public static string Render(IReadOnlyList<EvalRow> rows, int totalUp, int totalDown, int minRelevance, decimal spent, decimal maxUsd, bool reads, string? stoppedBecause)
    {
        var up = rows.Where(r => r.Vote > 0).ToList();
        var down = rows.Where(r => r.Vote < 0).ToList();
        var sb = new StringBuilder("# feed-eater prompt eval\n\n");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Votes in the archive: {totalUp} up, {totalDown} down. Evaluated: {up.Count} up, {down.Count} down. Spent ${spent:0.0000} of ${maxUsd:0.00}.");
        if (stoppedBecause is not null)
        {
            sb.AppendLine().AppendLine($"Stopped early: {stoppedBecause}.");
        }

        if (up.Count < MinUp || down.Count < MinDown)
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture,
                $"**Too few votes to mean anything.** At least {MinUp} up and {MinDown} down are needed in the sample; there are {up.Count} up and {down.Count} down. The numbers below are shown so the harness can be checked, not to compare prompts.");
        }

        if (rows.Count == 0)
        {
            return sb.AppendLine().AppendLine("Nothing was evaluated.").ToString();
        }

        sb.AppendLine().AppendLine("## Relevance against votes (current prompts)").AppendLine();
        sb.AppendLine("| | up | down |").AppendLine("|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| mean relevance (0-3) | {Mean(up.Select(r => r.NewRelevance))} | {Mean(down.Select(r => r.NewRelevance))} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| passes the digest bar (relevance >= {minRelevance}) | {Share(up.Count(r => r.NewRelevance >= minRelevance), up.Count)} | {Share(down.Count(r => r.NewRelevance >= minRelevance), down.Count)} |");
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Pairwise agreement (a liked item scores above a disliked one; ties count half): {Auc(up, down)}");
        var withOld = rows.Where(r => r.OldRelevance is not null).ToList();
        if (withOld.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"\nSame measure for the stored verdicts of the time ({withOld.Count} items that had one): {Auc(withOld.Where(r => r.Vote > 0).Select(r => r.OldRelevance!.Value), withOld.Where(r => r.Vote < 0).Select(r => r.OldRelevance!.Value))}");
        }

        sb.AppendLine().AppendLine("## Kind").AppendLine().AppendLine("| kind | stored | now |").AppendLine("|---|---|---|");
        foreach (var kind in new[] { "improve", "new", "fyi" })
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {kind} | {rows.Count(r => r.OldKind == kind)} | {rows.Count(r => r.NewKind == kind)} |");
        }

        var newNow = rows.Where(r => r.NewKind == "new").ToList();
        var newThen = rows.Where(r => r.OldKind == "new").ToList();
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture,
            $"\"new\" with no version or date in the item's text: {NoEvidence(newThen)} stored, {NoEvidence(newNow)} now.");

        if (reads)
        {
            var summaries = rows.Where(r => r.NewSummary is not null).ToList();
            var claimsNow = summaries.Count(r => ClaimsNovelty(r.NewSummary!) && !HasEvidence(r.Text));
            var claimsThen = rows.Count(r => r.OldSummary is not null && ClaimsNovelty(r.OldSummary) && !HasEvidence(r.Text));
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"\nSummaries that call something new, launched or recent with no version or date in the text: {claimsThen} stored, {claimsNow} of {summaries.Count} now.");
        }

        sb.AppendLine().AppendLine("## Largest disagreements").AppendLine();
        foreach (var r in rows.Where(r => r.Vote > 0 && r.NewRelevance <= 1).Take(5).Concat(rows.Where(r => r.Vote < 0 && r.NewRelevance >= minRelevance).Take(5)))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {(r.Vote > 0 ? "up" : "down")}, relevance {r.NewRelevance}, kind {r.NewKind}: {Clean(r.Title)} (item {r.Id})");
        }

        return sb.ToString();
    }

    /// <summary>True when the item's own text carries a version, a year or a date that could back a claim of novelty.</summary>
    public static bool HasEvidence(string text) => Safe(() => Evidence().IsMatch(text));

    public static bool ClaimsNovelty(string summary) => Safe(() => Novelty().IsMatch(summary));

    private static bool Safe(Func<bool> match)
    {
        try
        {
            return match();
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    internal static string Auc(IReadOnlyCollection<EvalRow> up, IReadOnlyCollection<EvalRow> down) =>
        Auc(up.Select(r => r.NewRelevance), down.Select(r => r.NewRelevance));

    internal static string Auc(IEnumerable<int> up, IEnumerable<int> down)
    {
        var (u, d) = (up.ToList(), down.ToList());
        if (u.Count == 0 || d.Count == 0)
        {
            return "n/a (needs both up and down votes)";
        }

        var score = u.Sum(a => d.Sum(b => a > b ? 1.0 : a == b ? 0.5 : 0.0)) / ((double)u.Count * d.Count);
        return score.ToString("0.00", CultureInfo.InvariantCulture) + " (0.50 is chance)";
    }

    private static string NoEvidence(IReadOnlyCollection<EvalRow> rows) =>
        rows.Count == 0 ? "0 of 0" : $"{rows.Count(r => !HasEvidence(r.Text))} of {rows.Count}";

    private static string Mean(IEnumerable<int> values) => values.Any() ? values.Average().ToString("0.00", CultureInfo.InvariantCulture) : "n/a";

    private static string Share(int n, int of) => of == 0 ? "n/a" : $"{n} of {of} ({100.0 * n / of:0}%)";

    private static string Clean(string title) => title.Replace('\n', ' ').Replace('|', '/');

    [GeneratedRegex(@"\bv?\d+\.\d+(\.\d+)?\b|\b20\d\d\b|\b(released|release notes|changelog|version)\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Evidence();

    [GeneratedRegex(@"\b(new|newly|launch(ed|es)?|just (released|released)|recently|introduc(es|ed)|unveil(s|ed)|debut(s|ed)?)\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Novelty();
}
