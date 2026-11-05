using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using FeedEater.Issues;
using FeedEater.PriorArt.Peers;

namespace FeedEater.WeeklyIdeas;

/// <summary>Where the weekly run gets its <c>peer-gap</c> leads; a seam so the job test needs no prior-art base.</summary>
public interface IPeerGapSource
{
    /// <summary>The best gaps of the peer set whose source URL is not in <paramref name="skip"/>; empty when no set is configured.</summary>
    Task<IReadOnlyList<Lead>> LeadsAsync(IReadOnlySet<string> skip, CancellationToken ct);
}

/// <summary>Turns a repo's gap report into weekly-ideas leads: the top rows by peers × mean confidence, each with the peers and an evidence link apiece.</summary>
public static class PeerGapLeads
{
    public const string Signal = "peer-gap";

    /// <summary>Most gap leads per peer set and run; the weekly run files at most five ideas, so gaps take at most two of them.</summary>
    public const int PerSet = 2;

    public static bool IsGap(Lead lead) => lead.Signal == Signal;

    public static string Url(string repo, string slug) => $"feed-eater://gaps/{repo}/{slug}";

    public static IReadOnlyList<Lead> Select(string repo, GapReport report, IReadOnlySet<string> skip, int max = PerSet) =>
        report.Gaps.Where(g => g.Peers.Count > 0 && !skip.Contains(Url(repo, g.Slug)))
            .OrderByDescending(g => g.Peers.Count * g.Peers.Average(p => p.Confidence)).ThenBy(g => g.Feature, StringComparer.Ordinal)
            .Take(max).Select(g => ToLead(repo, g)).ToList();

    private static Lead ToLead(string repo, GapRow gap)
    {
        var names = string.Join(", ", gap.Peers.Select(p => p.Tool));
        var best = gap.Peers.OrderByDescending(p => p.Confidence).First();
        var detail = string.Create(CultureInfo.InvariantCulture, $"{gap.Feature}: {gap.Peers.Count} peers have it ({names}), {repo} lacks it. E.g. {best.Tool}: {best.Text}");

        var extra = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Peers with {Markdown.Text(gap.Feature)} ({repo} lacks it):"));
        foreach (var p in gap.Peers)
        {
            var link = EvidenceUrl(p);
            extra.Append("\n- ").Append(link is null ? Markdown.Text(p.Tool) : Markdown.Link(p.Tool, link)).Append(": ").Append(Markdown.Text(p.Text));
        }

        return new Lead(Signal, gap.Feature, Url(repo, gap.Slug), detail, repo, extra.ToString());
    }

    /// <summary>A code link at the cited commit, else the evidence's own URL, else the peer's repo page.</summary>
    internal static string? EvidenceUrl(PeerFeature p)
    {
        var e = p.Evidence;
        if (e.Repo is { Length: > 0 } repo && e.Commit is { Length: > 0 } commit && e.Path is { Length: > 0 } path)
        {
            return $"https://github.com/{repo}/blob/{commit}/{path}{LineAnchor(e.Lines)}";
        }

        if (e.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return url;
        }

        return p.ToolRef is { } r && r.StartsWith("gh:", StringComparison.OrdinalIgnoreCase) ? "https://github.com/" + r[3..] : null;
    }

    private static string LineAnchor(string? lines)
    {
        if (string.IsNullOrWhiteSpace(lines))
        {
            return "";
        }

        var parts = lines.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!parts.All(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return "";
        }

        return parts.Length == 2 ? $"#L{parts[0]}-L{parts[1]}" : $"#L{parts[0]}";
    }
}

public sealed class PeerGapSource(PriorArtGaps gaps, PeerSetClient peerSet, ILogger<PeerGapSource> logger, IOptions<FeedEaterOptions> options) : IPeerGapSource
{
    private const int MinPeers = 2;

    public async Task<IReadOnlyList<Lead>> LeadsAsync(IReadOnlySet<string> skip, CancellationToken ct)
    {
        if (!options.Value.PeerHarvest.Enabled)
        {
            return [];
        }

        try
        {
            var text = await peerSet.TextAsync(ct);
            if (text is null)
            {
                return [];
            }

            var set = PeerSetParser.Parse(text);
            var report = await gaps.QueryAsync(set.Repo, null, MinPeers, null, ct);
            return PeerGapLeads.Select(set.Repo, report, skip);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Peer gaps unavailable; this week's ideas go without them");
            return [];
        }
    }
}
