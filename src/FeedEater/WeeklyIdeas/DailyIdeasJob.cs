using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Issues;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.WeeklyIdeas;

/// <summary>
/// Every day at <c>DailyIdeas:At</c>: the last 24 hours' improve items, likes, saves and bookmarks become at most <c>DailyIdeas:MaxIdeas</c> ideas,
/// staged like the weekly ones. A source staged before is not offered again, and a day with nothing new asks the model nothing and sends nothing.
/// Peer gaps stay with the weekly run.
/// </summary>
public sealed class DailyIdeasJob(
    WeeklyIdeaStore store, ProfileStore profiles, GitHubIssues issues, BacklogClient backlog, LiteLlmClient llm, TelegramClient telegram, InboxStore inboxStore, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<DailyIdeasJob> logger)
    : IdeasJob(store, null, profiles, issues, backlog, llm, telegram, inboxStore, quiet, cursors, options, health, time, logger)
{
    protected override string Name => "daily-ideas";
    protected override TimeSpan Window => TimeSpan.FromHours(24);
    protected override int MaxIdeas => Settings.DailyIdeas.MaxIdeas;
    protected override string Span => "day";
    protected override bool OnlyUnstaged => true;
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, Settings.DailyIdeas.At);
    protected override string Label(string key) => key;
}
