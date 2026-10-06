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
/// past the anonymous request budget or the rate limit) is left for next month's autopsy; only the rate limit and an unreachable GitHub end the run.
/// </summary>
public sealed class AutopsyJob(
    AutopsyStore store, GitHubStarsClient github, TelegramClient telegram, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<AutopsyJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "autopsy";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestMonthly(localNow, 1, new TimeSpan(9, 0, 0));

    protected override async Task<bool> HoldAsync(CancellationToken ct) => await quiet.IsQuietAsync(ct);

    /// <summary>An autopsy that was saved but not sent (the send failed) is only sent again: asking GitHub again could not rebuild it.</summary>
    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var html = await store.GetAsync(key, ct) is { SentAt: null } unsent ? unsent.Message : await BuildAndSaveAsync(key, ct);
        if (html is null)
        {
            Logger.LogInformation("No 👍 item reached its autopsy this month; no report");
            return;
        }

        await telegram.SendAsync(Settings.Telegram.AllowedUserId, new OutMessage(html), ct);
        await store.MarkSentAsync(key, Time.GetUtcNow(), ct);
    }

    private async Task<string?> BuildAndSaveAsync(string key, CancellationToken ct)
    {
        var at = Time.GetUtcNow();
        if (await BuildAsync(key, at, ct) is not { } report)
        {
            return null;
        }

        var html = AutopsyFormatter.Message(key, report).Html;
        await store.SaveAsync(key, at, report, html, ct);
        return html;
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

            RepoLookup lookup;
            try
            {
                lookup = await LookupAsync(s.Repo, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                Logger.LogWarning(ex, "GitHub unreachable; the rest wait for next month");
                stopped = true;
                notChecked++;
                continue;
            }

            requests += lookup.Requests;
            if (lookup.Outcome == LookupOutcome.RateLimited)
            {
                Logger.LogWarning("GitHub rate limit reached; the rest wait for next month");
                stopped = true;
                notChecked++;
                continue;
            }

            if (lookup.Outcome == LookupOutcome.Failed)
            {
                Logger.LogWarning("GitHub failed for {Repo}; left for next month", s.Repo);
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

    /// <summary>An answer this does not read is Failed like a refused call; an unreachable or slow GitHub still throws.</summary>
    private async Task<RepoLookup> LookupAsync(string fullName, CancellationToken ct)
    {
        var parts = fullName.Split('/', 2);
        try
        {
            return await github.LookupAsync(parts[0], parts[1], ct);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            Logger.LogWarning(ex, "GitHub facts for {Repo} unreadable; left for next month", fullName);
            return new RepoLookup(LookupOutcome.Failed, null, RepoSnapshotJob.RequestsPerLookup);
        }
    }
}
