using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FeedEater.Storage;

namespace FeedEater.Ranking;

/// <summary>The identity of a ranker: the same weights, learned switch and profile text give the same id, so votes can be compared across changes.</summary>
public static class RankerId
{
    public static string Compute(WeightsOptions w, bool learned, IEnumerable<Profile> profiles)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{w.Taste}|{w.Prior}|{w.MinPositives}|{w.MinFeedVotes}|{w.Learned}|{(learned ? 1 : 0)}");
        foreach (var p in profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append('\n').Append(p.Key).Append('\t').Append(p.Description);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..8].ToLowerInvariant();
    }
}
