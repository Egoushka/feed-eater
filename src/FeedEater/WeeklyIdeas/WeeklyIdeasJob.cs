using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Issues;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.WeeklyIdeas;

/// <summary>
/// Mondays at <c>WeeklyIdeas:At</c>: the week's improve items, imported research notes, what was liked, saved or bookmarked, and the top peer gaps become a ranked shortlist
/// of at most <c>WeeklyIdeas:MaxFiled</c> ideas. With the daily run on, sources a daily run already staged are left out.
/// </summary>
public sealed class WeeklyIdeasJob(
    WeeklyIdeaStore store, IPeerGapSource gapSource, ProfileStore profiles, GitHubIssues issues, BacklogClient backlog, LiteLlmClient llm, TelegramClient telegram, InboxStore inboxStore, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<WeeklyIdeasJob> logger)
    : IdeasJob(store, gapSource, profiles, issues, backlog, llm, telegram, inboxStore, quiet, cursors, options, health, time, logger)
{
    /// <summary>Set by /ideas: the next tick runs the shortlist now, window or not, once.</summary>
    public const string ForceCursor = "weekly-ideas:force";

    protected override string Name => "weekly-ideas";
    protected override TimeSpan Window => TimeSpan.FromDays(7);
    protected override int MaxIdeas => Settings.WeeklyIdeas.MaxFiled;
    protected override string Span => "week";
    protected override bool OnlyUnstaged => Settings.DailyIdeas.Enabled;
    protected override string? DueKey(DateTime localNow) => Schedule.LatestWeekly(localNow, DayOfWeek.Monday, Settings.WeeklyIdeas.At);

    protected override string Label(string key)
    {
        var monday = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(monday)}-W{ISOWeek.GetWeekOfYear(monday):00}");
    }

    protected override async Task PollAsync(CancellationToken ct)
    {
        if (await Cursors.GetAsync(ForceCursor, ct) is null)
        {
            await base.PollAsync(ct);
            return;
        }

        try
        {
            await RunAsync(Schedule.LatestWeekly(Schedule.Local(Time.GetUtcNow(), Settings.Zone), DayOfWeek.Monday, Settings.WeeklyIdeas.At), ct);
        }
        finally
        {
            await Cursors.DeleteAsync(ForceCursor, CancellationToken.None);
        }
    }
}
