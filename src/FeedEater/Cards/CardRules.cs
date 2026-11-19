using System.Text.RegularExpressions;
using FeedEater.Ui;

namespace FeedEater.Cards;

public sealed record EvidenceLink(string Url, string Title, string Source, DateTime? At);

/// <summary>One thing seen about an inventory item. <c>VersionBump</c> signals are release notices; they never make a card alone.</summary>
public sealed record CardSignal(string Source, string Url, string Title, bool VersionBump, long? ItemId, DateTime? At, int Weight);

public sealed record CardCandidate(string Term, IReadOnlyList<InventoryItem> Usages, IReadOnlyList<CardSignal> Signals);

public sealed record NewCard(string Term, string Title, string What, string Repo, string WhereInStack, IReadOnlyList<EvidenceLink> Evidence, string Effort, string Gain, IReadOnlyList<long> SourceSignalIds);

public sealed record CardRow(
    long Id, string Term, string Title, string What, string Repo, string WhereInStack, IReadOnlyList<EvidenceLink> Evidence, bool EvidenceChecked,
    string Effort, string Gain, string State, string StateReason, DateTime CreatedAt, DateTime? StateChangedAt, IReadOnlyList<long> SourceSignalIds);

public static partial class CardRules
{
    public static readonly string[] States = ["proposed", "assess", "trial", "adopt", "hold", "dropped"];
    public const int MaxEvidence = 5;
    public const int MaxUsages = 5;
    public const int MaxReason = 500;
    private const int MinTermLength = 4;

    /// <summary>Why a card cannot be stored, or null. A card needs a place in the stack and at least one http(s) evidence URL.</summary>
    public static string? Validate(NewCard c)
    {
        if (string.IsNullOrWhiteSpace(c.Term) || string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.What) || string.IsNullOrWhiteSpace(c.Repo))
        {
            return "a card needs a term, a title, a description and a repo.";
        }

        if (string.IsNullOrWhiteSpace(c.WhereInStack))
        {
            return "a card needs where_in_stack.";
        }

        return c.Evidence.Any(e => Html.SafeUrl(e.Url) is not null) ? null : "a card needs at least one evidence URL.";
    }

    /// <summary>The state to write, or an error: one of <see cref="States"/>, with a reason that says something.</summary>
    public static string? CheckTransition(string state, string? reason) =>
        !States.Contains(state, StringComparer.Ordinal) ? $"unknown state '{state}'; use one of {string.Join(", ", States)}."
        : string.IsNullOrWhiteSpace(reason) ? "reason is required."
        : reason.Trim().Length > MaxReason ? $"reason is longer than {MaxReason} characters."
        : null;

    /// <summary>Terms too generic to search for: framework parts and short words.</summary>
    public static bool Searchable(string term) =>
        term.Length >= MinTermLength && !term.StartsWith("Microsoft.", StringComparison.Ordinal) && !term.StartsWith("System.", StringComparison.Ordinal);

    public static bool Mentions(string title, string term) =>
        Regex.IsMatch(title, $"(?<![A-Za-z0-9]){Regex.Escape(term)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>True when the title says nothing about the term but a version and a release word: "Dapper 2.1.66 released".</summary>
    public static bool IsVersionBump(string title, string term)
    {
        var rest = Regex.Replace(title, Regex.Escape(term), " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        rest = ReleaseWords().Replace(rest, " ");
        rest = Regex.Replace(rest, @"\bv?\d+(\.\d+)+\S*", " ", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        return !Regex.IsMatch(rest, "[A-Za-z]{2,}", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>The card for a candidate, or null: no usage, or no signal that is more than a version bump.</summary>
    public static NewCard? Build(CardCandidate c)
    {
        var real = c.Signals.Where(s => !s.VersionBump && Html.SafeUrl(s.Url) is not null).OrderByDescending(s => s.Weight).ToList();
        if (real.Count == 0 || c.Usages.Count == 0)
        {
            return null;
        }

        var top = real[0];
        var home = c.Usages.GroupBy(u => u.Repo).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        var where = string.Join("; ", c.Usages.OrderBy(u => u.Repo, StringComparer.Ordinal).ThenBy(u => u.SourcePath, StringComparer.Ordinal).Take(MaxUsages)
            .Select(u => $"{u.Repo}: {u.Name}{(u.Version.Length > 0 ? " " + u.Version : "")} ({u.Kind}, {u.SourcePath})"))
            + (c.Usages.Count > MaxUsages ? $"; and {c.Usages.Count - MaxUsages} more" : "");
        var advisory = real.Any(s => s.Source == "deps.dev");
        var card = new NewCard(
            c.Term.ToLowerInvariant(),
            Clip($"{c.Term}: {top.Title}", 200),
            $"{real.Count} signal{(real.Count == 1 ? "" : "s")} about {c.Term}, strongest from {top.Source}: \"{Clip(top.Title, 160)}\". Only the titles were read; the pages were not (UNKNOWN until read).",
            home, where,
            real.Take(MaxEvidence).Select(s => new EvidenceLink(Html.SafeUrl(s.Url)!, Clip(s.Title, 200), s.Source, s.At)).ToList(),
            advisory ? "small: read the advisory, check the running version" : "small: read the evidence, then assess or hold",
            advisory ? $"Know whether {c.Term} in your stack is exposed." : $"A decision on {c.Term} backed by evidence instead of a headline.",
            real.Where(s => s.ItemId is not null).Select(s => s.ItemId!.Value).Distinct().ToList());
        return Validate(card) is null ? card : null;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"\b(released?|releases|version|update[sd]?|changelog|now available|is out|available|of|the|is|out|now|v)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ReleaseWords();
}
