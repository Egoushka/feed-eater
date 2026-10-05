using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Ingest;

/// <summary>
/// Links items from different feeds that tell the same story to the first one seen. Only the link is written; no row is hidden,
/// so the archive and search keep every item and only the digest picks one per story.
/// </summary>
public sealed partial class StoryClusterer(ClusterStore store, IOptions<FeedEaterOptions> options, ILogger<StoryClusterer> logger)
{
    private const int Batch = 200;
    private const int Nearest = 5;

    /// <summary>Considers every pending item; returns how many joined a cluster.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var o = options.Value.Cluster;
        var linked = 0;
        while (await store.PendingAsync(Batch, ct) is { Count: > 0 } pending)
        {
            foreach (var item in pending)
            {
                var heads = await store.NearestAsync(item.Id, o.Threshold, o.WindowDays, Nearest, ct);
                var head = heads.FirstOrDefault(h => SameStory(item.Title, h.Title));
                await store.SetAsync(item.Id, head?.Id, ct);
                if (head is not null)
                {
                    linked++;
                }
            }
        }

        if (linked > 0)
        {
            logger.LogInformation("Linked {Linked} items to an earlier story", linked);
        }

        return linked;
    }

    /// <summary>Two titles that each name a version, and not the same ones, are different releases of one product, not one story.</summary>
    internal static bool SameStory(string a, string b)
    {
        var (x, y) = (Versions(a), Versions(b));
        return x.Count == 0 || y.Count == 0 || x.SetEquals(y);
    }

    private static HashSet<string> Versions(string title) =>
        VersionPattern().Matches(title).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"(?<![\w.])v?(\d+(?:\.\d+)+(?:-[0-9A-Za-z.]+)?)")]
    private static partial Regex VersionPattern();
}
