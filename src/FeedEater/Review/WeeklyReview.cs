using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Inbox;
using FeedEater.Loops;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Review;

/// <summary>Sundays at 18:30 local time: the week's figures to Telegram and into /ui/weekly. Runs once per Sunday; a missed one runs at start.</summary>
public sealed class WeeklyReview(
    WeeklyStore weekly, IInbox inbox, QuietHours quiet, TasteSwitch taste,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<WeeklyReview> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "weekly-review";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestWeekly(localNow, DayOfWeek.Sunday, new TimeSpan(18, 30, 0));

    protected override async Task<bool> HoldAsync(CancellationToken ct) => await quiet.IsQuietAsync(ct);

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

        var message = WeeklyFormatter.Message(key, report, zone, await taste.SuggestionAsync(ct));
        await weekly.SaveAsync(key, until, report, message.Html, ct);
        await inbox.AddAsync(new InboxEntry($"review:{key}", InboxKinds.Review, Payload: new JsonObject { ["html"] = message.Html },
            ExpiresAt: Time.GetUtcNow().AddDays(7)), message, ct);
        await weekly.MarkSentAsync(key, Time.GetUtcNow(), ct);
    }
}
