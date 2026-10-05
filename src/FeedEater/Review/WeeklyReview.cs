using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Review;

/// <summary>Sundays at 18:30 Kyiv: the week's figures to Telegram and into /ui/weekly. Runs once per Sunday; a missed one runs at start.</summary>
public sealed class WeeklyReview(
    WeeklyStore weekly, TelegramClient telegram,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<WeeklyReview> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "weekly-review";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestWeekly(localNow, DayOfWeek.Sunday, new TimeSpan(18, 30, 0));

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var until = Time.GetUtcNow();
        var since = until.AddDays(-7);
        var zone = Settings.Zone;
        var report = await weekly.BuildAsync(since, until, Schedule.Key(Schedule.Local(since, zone).Date), Schedule.Key(Schedule.Local(until, zone).Date), ct);
        if (report.Items == 0 && report.Shown == 0 && report.Up == 0 && report.Down == 0)
        {
            Logger.LogInformation("Nothing happened this week; no review");
            return;
        }

        var message = WeeklyFormatter.Message(key, report, zone);
        await weekly.SaveAsync(key, until, report, message.Html, ct);
        await telegram.SendAsync(Settings.Telegram.AllowedUserId, message, ct);
        await weekly.MarkSentAsync(key, Time.GetUtcNow(), ct);
    }
}
