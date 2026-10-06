using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Signals;
using FeedEater.Storage;

namespace FeedEater.Hype;

/// <summary>
/// Daily at 05:00: every 👍 item without a snapshot gets one, so the autopsy has a starting point. Without <c>GitHub:Token</c> a run
/// stops at <see cref="RequestsPerRun"/> requests (the anonymous limit is 60 an hour) and the next day carries on; a rate limit or an
/// unreachable GitHub also ends the run cleanly. Any other refusal skips that repo; the third one in a row gives it an empty snapshot.
/// </summary>
public sealed class RepoSnapshotJob(
    GitHubStarsClient github, AutopsyStore store, CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time,
    ILogger<RepoSnapshotJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    internal const int RequestsPerRun = 40;
    internal const int RequestsPerLookup = 2;   // the repo, then its latest release
    private const int MaxFailures = 3;
    private const int MaxCandidates = 500;

    protected override string Name => "repo-snapshots";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, new TimeSpan(5, 0, 0));

    protected override async Task RunAsync(string key, CancellationToken ct) =>
        Logger.LogInformation("Took {Count} repo snapshots", await SnapshotAsync(ct));

    internal async Task<int> SnapshotAsync(CancellationToken ct)
    {
        var budget = Settings.GitHub.Token.Length > 0 ? int.MaxValue : RequestsPerRun;
        var requests = 0;
        var taken = 0;
        foreach (var c in await store.CandidatesAsync(MaxCandidates, ct))
        {
            var at = Time.GetUtcNow();
            if (GitHubRepos.Find(c.Url, c.Content) is not var (owner, repo))
            {
                await store.AddAsync(new RepoSnapshot(c.ItemId, null, at, null, null, null, null), ct);
                taken++;
                continue;
            }

            if (requests > budget - RequestsPerLookup)
            {
                Logger.LogInformation("Snapshot request budget ({Budget}) reached; the next daily run continues", budget);
                break;
            }

            RepoLookup lookup;
            try
            {
                lookup = await github.LookupAsync(owner, repo, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                Logger.LogWarning(ex, "GitHub unreachable; snapshots continue on the next daily run");
                break;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
            {
                Logger.LogWarning(ex, "GitHub answered for {Owner}/{Repo} in a shape this does not read; skipped", owner, repo);
                requests += RequestsPerLookup;
                continue;
            }

            requests += lookup.Requests;
            if (lookup.Outcome == LookupOutcome.RateLimited)
            {
                Logger.LogWarning("GitHub rate limit reached; snapshots continue on the next daily run");
                break;
            }

            if (lookup.Outcome == LookupOutcome.Failed)
            {
                var failures = await store.AddFailureAsync(c.ItemId, ct);
                Logger.LogWarning("GitHub failed for {Owner}/{Repo} ({Failures} of {Max}); skipped", owner, repo, failures, MaxFailures);
                if (failures >= MaxFailures)
                {
                    await store.AddAsync(new RepoSnapshot(c.ItemId, $"{owner}/{repo}", at, null, null, null, null), ct);
                    taken++;
                }

                continue;
            }

            var facts = lookup.Facts;
            await store.AddAsync(new RepoSnapshot(c.ItemId, $"{owner}/{repo}", at, facts?.Stars, facts?.PushedAt, facts?.ReleaseTag, facts?.CreatedAt), ct);
            taken++;
        }

        return taken;
    }
}
