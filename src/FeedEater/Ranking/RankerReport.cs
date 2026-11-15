using System.Globalization;
using System.Text;

namespace FeedEater.Ranking;

/// <summary>Votes (👍/👎) and swipes (like/dislike) on items, grouped by the ranker that scored them.</summary>
public sealed record RankerRow(string Ranker, DateTimeOffset FirstAt, int Up, int Down, int Likes, int Dislikes)
{
    public const int MinVotes = 10;
    public int Votes => Up + Down;
    public double? HitRate => Votes == 0 ? null : (double)Up / Votes;
}

public static class RankerReport
{
    public static string Render(IReadOnlyList<RankerRow> rows)
    {
        var sb = new StringBuilder("# feed-eater ranker report\n\nHit rate = 👍 share of votes on items that ranker scored. Swipe = like share of swipes.\n\n");
        if (rows.Count == 0)
        {
            return sb.Append("No votes on scored items yet.\n").ToString();
        }

        sb.AppendLine("| ranker | first vote | votes | hit rate | swipes | swipe like rate |").AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows.OrderBy(r => r.FirstAt))
        {
            var rate = r.HitRate is { } h ? $"{h:P0}{(r.Votes < RankerRow.MinVotes ? " (too few)" : "")}" : "-";
            var swipes = r.Likes + r.Dislikes;
            var swipe = swipes == 0 ? "-" : $"{(double)r.Likes / swipes:P0}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Ranker} | {r.FirstAt:yyyy-MM-dd} | {r.Votes} | {rate} | {swipes} | {swipe} |");
        }

        return sb.ToString();
    }
}
