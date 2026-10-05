using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Memory;

/// <summary>Sundays at 18:00: one plain summary of the week's reading to the learning bank. No model call; Hindsight extracts facts itself.</summary>
public sealed class WeeklyRetain(
    HindsightClient hindsight, FeedbackStore feedback,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<WeeklyRetain> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private static readonly string[] Tags = ["signal:feed-eater", "trust:agent", "type:weekly-log"];

    protected override string Name => "weekly-retain";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestWeekly(localNow, DayOfWeek.Sunday, new TimeSpan(18, 0, 0));

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var now = Time.GetUtcNow();
        var report = await feedback.WeekReportAsync(now.AddDays(-7), ct);
        if (report.Digests == 0)
        {
            Logger.LogInformation("No digests this week; nothing to retain");
            return;
        }

        var sunday = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var week = string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(sunday)}-W{ISOWeek.GetWeekOfYear(sunday):00}");
        await hindsight.RetainAsync(Settings.Hindsight.Bank, Text(week, report), "feed-eater weekly reading",
            TimeZoneInfo.ConvertTime(now, Settings.Zone), $"feed-eater-{week}", Tags, ct);
    }

    internal static string Text(string week, WeekReport r)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"feed-eater week {week}: {r.Digests} digests with {r.Highlights} highlights. Yehor voted up {r.Up}, down {r.Down}.");
        if (r.Liked.Count > 0)
        {
            text += $" Liked: {string.Join("; ", r.Liked)}.";
        }

        if (r.Ideas.Count > 0)
        {
            text += $" Ideas filed in Plane: {string.Join("; ", r.Ideas.Select(i => $"{i.Title} ({i.PlaneProject})"))}.";
        }

        return text;
    }
}
