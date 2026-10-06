using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Ui;

/// <summary>One point on the map. <c>X</c> and <c>Y</c> are 0 to 1; <c>Vote</c> is 1, -1 or 0 (none yet in the chosen month); <c>Weight</c> is the vote's.</summary>
public sealed record MapDot(long Id, string Title, double X, double Y, int Vote, double Weight);

public sealed record MapLabel(string Key, double X, double Y);

/// <summary>A cell of the grid: column and row, counted from the top left.</summary>
public readonly record struct MapCell(int Column, int Row);

public sealed record MapTally(string Key, int Up, int Down, int Unvoted);

public sealed record MapView(
    DateOnly Month, DateOnly CurrentMonth, IReadOnlyList<DateOnly> Months, IReadOnlyList<MapDot> Dots, IReadOnlyList<MapLabel> Labels,
    IReadOnlyList<MapCell> Unseen, IReadOnlyList<MapTally> Profiles)
{
    public int Up => Dots.Count(d => d.Vote > 0);
    public int Down => Dots.Count(d => d.Vote < 0);
    public int Unvoted => Dots.Count(d => d.Vote == 0);
}

/// <summary>
/// The data behind <c>/ui/map</c>: every voted item and a seeded sample of recent ones, projected to a plane. The layout never depends on
/// the month, only the votes shown do, so stepping through months keeps every item where it was. The items and their projection are built
/// once for all months and kept for ten minutes; requests that find the cache cold wait for the one build.
/// </summary>
public sealed class TasteMap(MapStore store, ProfileStore profiles, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    public const int Grid = 16;
    private const int Sample = 1500;
    private const int RecentDays = 90;
    private const int LabelNearest = 10;
    private const int StripMonths = 24;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary><paramref name="Spots"/> is empty when there are too few items to project.</summary>
    private sealed record Layout(IReadOnlyList<MapItem> Rows, IReadOnlyList<float[]> Vectors, IReadOnlyList<Point2> Spots, IReadOnlyList<Profile> Profiles);

    private sealed record Cached(DateTimeOffset At, Layout Layout);

    private readonly SemaphoreSlim _building = new(1, 1);
    private volatile Cached? _cached;

    /// <summary>The month as <c>yyyy-MM</c> exactly (no sign, spaces or single-digit months); anything else, a year before 2000 or a month after the current one gives the current month.</summary>
    public static DateOnly ParseMonth(string? text, DateOnly current) =>
        text is { Length: 7 }
        && DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
        && month.Year >= 2000 && month <= current && Text(month) == text
            ? month
            : current;

    public static string Text(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public async Task<MapView> GetAsync(string? month, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var current = MonthOf(TimeZoneInfo.ConvertTime(now, options.Value.Zone));
        return Overlay(await LayoutAsync(now, ct), ParseMonth(month, current), current, options.Value.Zone);
    }

    private async Task<Layout> LayoutAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_cached is { } hit && now - hit.At < Ttl)
        {
            return hit.Layout;
        }

        await _building.WaitAsync(ct);
        try
        {
            if (_cached is { } built && now - built.At < Ttl)
            {
                return built.Layout;   // another request built it while this one waited
            }

            var layout = await BuildLayoutAsync(now, ct);
            _cached = new Cached(now, layout);
            return layout;
        }
        finally
        {
            _building.Release();
        }
    }

    private async Task<Layout> BuildLayoutAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rows = await store.ItemsAsync(now.AddDays(-RecentDays), Sample, ct);
        var vectors = rows.Select(r => Vectors.Normalize(r.Embedding)).ToList();
        var coords = Projection.Project(vectors);
        return coords.Count == 0 ? new Layout(rows, vectors, [], []) : new Layout(rows, vectors, Fit(coords), await profiles.AllAsync(ct));
    }

    /// <summary>The votes of one month on the fixed layout.</summary>
    private static MapView Overlay(Layout layout, DateOnly month, DateOnly current, TimeZoneInfo zone)
    {
        var rows = layout.Rows;
        var months = Strip(rows, current, zone);
        if (layout.Spots.Count == 0)
        {
            return new MapView(month, current, months, [], [], [], []);
        }

        var next = month.AddMonths(1).ToDateTime(TimeOnly.MinValue);
        var end = new DateTimeOffset(next, zone.GetUtcOffset(next)).UtcDateTime;
        var dots = new List<MapDot>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var voted = r.Vote is not null && r.VotedAt is { } at && DateTime.SpecifyKind(at, DateTimeKind.Utc) < end;
            dots.Add(new MapDot(r.Id, r.Title, layout.Spots[i].X, layout.Spots[i].Y, voted ? r.Vote!.Value : 0, voted ? r.Weight : 1));
        }

        var (labels, tallies) = Profiles(layout.Profiles, layout.Vectors, dots);
        return new MapView(month, current, months, dots, labels, UnseenCells(dots, rows), tallies);
    }

    /// <summary>Scales to the unit square with the same factor on both axes, so distances keep their proportions.</summary>
    private static List<Point2> Fit(IReadOnlyList<Point2> coords)
    {
        var minX = coords.Min(c => c.X);
        var maxX = coords.Max(c => c.X);
        var minY = coords.Min(c => c.Y);
        var maxY = coords.Max(c => c.Y);
        var span = Math.Max(maxX - minX, maxY - minY);
        if (span < 1e-12)
        {
            span = 1;
        }

        return coords.Select(c => new Point2(0.5 + (c.X - (minX + maxX) / 2) / span, 0.5 + (c.Y - (minY + maxY) / 2) / span)).ToList();
    }

    /// <summary>Each profile key sits at the mean of its nearest points; every point also counts toward the one profile it is nearest to.</summary>
    private static (List<MapLabel> Labels, List<MapTally> Tallies) Profiles(IReadOnlyList<Profile> profiles, IReadOnlyList<float[]> vectors, IReadOnlyList<MapDot> dots)
    {
        var usable = profiles.Where(p => p.Embedding.Length == vectors[0].Length).ToList();
        var sims = usable.Select(p => { var unit = Vectors.Normalize(p.Embedding); return vectors.Select(v => Vectors.Dot(v, unit)).ToArray(); }).ToList();
        var labels = new List<MapLabel>();
        for (var j = 0; j < usable.Count; j++)
        {
            var near = Enumerable.Range(0, dots.Count).OrderByDescending(i => sims[j][i]).Take(LabelNearest).ToList();
            labels.Add(new MapLabel(usable[j].Key, near.Average(i => dots[i].X), near.Average(i => dots[i].Y)));
        }

        var up = new int[usable.Count];
        var down = new int[usable.Count];
        var none = new int[usable.Count];
        for (var i = 0; i < dots.Count && usable.Count > 0; i++)
        {
            var best = 0;
            for (var j = 1; j < usable.Count; j++)
            {
                if (sims[j][i] > sims[best][i])
                {
                    best = j;
                }
            }

            (dots[i].Vote > 0 ? up : dots[i].Vote < 0 ? down : none)[best]++;
        }

        return (labels, usable.Select((p, j) => new MapTally(p.Key, up[j], down[j], none[j])).ToList());
    }

    /// <summary>Cells that hold points, none of which a sent digest ever carried: the regions the ranking never brings you.</summary>
    private static List<MapCell> UnseenCells(IReadOnlyList<MapDot> dots, IReadOnlyList<MapItem> rows) =>
        dots.Select((d, i) => (Cell: new MapCell(Math.Min((int)(d.X * Grid), Grid - 1), Math.Min((int)(d.Y * Grid), Grid - 1)), rows[i].Shown))
            .GroupBy(x => x.Cell)
            .Where(g => g.All(x => !x.Shown))
            .Select(g => g.Key)
            .OrderBy(c => c.Row).ThenBy(c => c.Column)
            .ToList();

    /// <summary>From the month of the first vote (at most 24 back) to the current one.</summary>
    private static List<DateOnly> Strip(IReadOnlyList<MapItem> rows, DateOnly current, TimeZoneInfo zone)
    {
        var first = rows.Where(r => r.VotedAt is not null).Select(r => MonthOf(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.VotedAt!.Value, DateTimeKind.Utc), zone))).DefaultIfEmpty(current).Min();
        var start = first > current.AddMonths(1 - StripMonths) ? first : current.AddMonths(1 - StripMonths);
        var months = new List<DateOnly>();
        for (var m = start; m <= current; m = m.AddMonths(1))
        {
            months.Add(m);
        }

        return months;
    }

    private static DateOnly MonthOf(DateTimeOffset local) => new(local.Year, local.Month, 1);

    private static DateOnly MonthOf(DateTime local) => new(local.Year, local.Month, 1);
}
