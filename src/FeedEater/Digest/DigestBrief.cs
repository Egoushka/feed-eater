using FeedEater.Storage;

namespace FeedEater.Digest;

public sealed record BriefLine(string Project, int Count, string Text, long ItemId);

/// <summary>
/// "The day in brief" for the UI: one line per project, built from what the digest's reads already say. No model call:
/// items from muted feeds are left out; each read summary is already a model-written sentence, so another call would only paraphrase it at a cost per digest.
/// </summary>
public static class DigestBrief
{
    public const int MaxLines = 5;
    private const int MaxText = 160;

    /// <summary>Largest project groups first (ties by first appearance); each line is the group's first item's summary.</summary>
    public static IReadOnlyList<BriefLine> Build(IReadOnlyList<ItemView> items) => items
        .Where(i => !i.FeedMuted && !string.IsNullOrWhiteSpace(i.Summary))
        .Select((item, position) => (item, position))
        .GroupBy(x => string.IsNullOrEmpty(x.item.Project) ? "other" : x.item.Project)
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.position))
        .Take(MaxLines)
        .Select(g => new BriefLine(g.Key, g.Count(), Clip(g.OrderBy(x => x.position).First().item.Summary!.Trim()), g.OrderBy(x => x.position).First().item.Id))
        .ToList();

    private static string Clip(string text)
    {
        if (text.Length <= MaxText)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', MaxText - 1);
        return text[..(cut > MaxText / 2 ? cut : MaxText - 1)].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }
}
