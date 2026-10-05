using FeedEater.Storage;

namespace FeedEater.Digest;

/// <summary>The header figures the digest message and the web UI share.</summary>
public static class DigestStats
{
    /// <summary>The 1st of the current local month, as an instant.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var first = new DateTime(local.Year, local.Month, 1);
        return new DateTimeOffset(first, zone.GetUtcOffset(first));
    }

    public static double? UpRate(VoteCounts votes) => votes.Up + votes.Down == 0 ? null : (double)votes.Up / (votes.Up + votes.Down);
}
