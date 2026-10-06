using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Hype;

/// <summary>
/// The 1st of the month at 09:00: scores every 👍 item whose repo snapshot has reached <see cref="AutopsyRules.MinAgeDays"/> days and was
/// not scored yet, stores the result for /ui/autopsy and sends the summary. Needs no model. A repo GitHub cannot answer for (or a run
/// past the anonymous request budget) is left for next month's autopsy.
/// </summary>
public sealed class AutopsyJob(
    AutopsyStore store, GitHubStarsClient github, TelegramClient telegram, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<AutopsyJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "autopsy";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestMonthly(localNow, 1, new TimeSpan(9, 0, 0));

    protected override async Task<bool> HoldAsync(CancellationToken ct) => await quiet.IsQuietAsync(ct);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var at = Time.GetUtcNow();
        var report = await BuildAsync(key, at, ct);
        if (report is null)
        {
            Logger.LogInformation("No 👍 item reached its autopsy this month; no report");
            return;
        }

        var message = AutopsyFormatter.Message(key, report);
        await store.SaveAsync(key, at, report, message.Html, ct);
        await telegram.SendAsync(Settings.Telegram.AllowedUserId, message, ct);
        await store.MarkSentAsync(key, Time.GetUtcNow(), ct);
    }

    /// <summary>Null when nothing could be scored (a report of items without a repo says nothing); otherwise the snapshots it covers are marked as scored.</summary>
    internal async Task<AutopsyReport?> BuildAsync(string key, DateTimeOffset at, CancellationToken ct)
    {
        var budget = Settings.GitHub.Token.Length > 0 ? int.MaxValue : RepoSnapshotJob.RequestsPerRun;
        var requests = 0;
        var stopped = false;
        var items = new List<AutopsyItem>();
        var done = new List<long>();
        int noRepo = 0, noBaseline = 0, notChecked = 0;
        foreach (var c in await store.PendingAsync(key, at, Settings.Cluster.Threshold, ct))
        {
            var s = c.Snapshot;
            if (s.Repo is null)
            {
                noRepo++;
                done.Add(s.ItemId);
                continue;
            }

            if (s.Stars is not { } before)
            {
                noBaseline++;
                done.Add(s.ItemId);
                continue;
            }

            if (stopped || requests > budget - RepoSnapshotJob.RequestsPerLookup)
            {
                notChecked++;
                continue;
            }

            var lookup = await LookupAsync(s.Repo, ct);
            requests += lookup.Requests;
            if (lookup.Outcome is LookupOutcome.RateLimited or LookupOutcome.Failed)
            {
                Logger.LogWarning("GitHub refused or failed ({Outcome}); the rest wait for next month", lookup.Outcome);
                stopped = true;
                notChecked++;
                continue;
            }

            var j = AutopsyRules.Judge(s, lookup.Facts, c.Idea, c.Saved, c.RelatedLater, at);
            items.Add(new AutopsyItem(
                s.ItemId, c.Title, c.Url, c.Feed, s.Repo, c.Relevance, j.Verdict, before, j.StarsNow, j.StarsGrowthPercent, j.PushedRecently,
                j.NewRelease, j.Idea, j.Saved, j.RelatedLater));
            done.Add(s.ItemId);
        }

        if (items.Count == 0)
        {
            return null;
        }

        await store.MarkScoredAsync(done, key, ct);
        return AutopsyScorer.Build(items, noRepo, noBaseline, notChecked, at);
    }

    /// <summary>A call that failed outright (unreachable, slow, an answer this does not read) is Failed like a refused one.</summary>
    private async Task<RepoLookup> LookupAsync(string fullName, CancellationToken ct)
    {
        var parts = fullName.Split('/', 2);
        try
        {
            return await github.LookupAsync(parts[0], parts[1], ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Logger.LogWarning(ex, "GitHub facts for {Repo} unavailable; left for next month", fullName);
            return new RepoLookup(LookupOutcome.Failed, null, RepoSnapshotJob.RequestsPerLookup);
        }
    }
}
