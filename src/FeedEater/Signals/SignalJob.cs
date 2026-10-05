using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Signals;

/// <summary>Daily at 02:00: new Karakeep link saves and GitHub stars become positive examples for ranking.</summary>
public sealed class SignalJob(
    KarakeepClient karakeep, GitHubStarsClient github, SignalStore signals, LiteLlmClient llm,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<SignalJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private const int MaxPages = 5;

    protected override string Name => "signals";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, new TimeSpan(2, 0, 0));

    protected override async Task RunAsync(string key, CancellationToken ct) =>
        Logger.LogInformation("Collected {Count} new signals", await CollectAsync(ct));

    internal async Task<int> CollectAsync(CancellationToken ct)
    {
        var fresh = new List<(NewSignal Signal, string Text)>();
        var sources = new List<(string Name, Func<CancellationToken, Task<List<(NewSignal, string)>>> Fetch)>();
        if (Settings.Karakeep.Token.Length > 0)
        {
            sources.Add(("karakeep", NewBookmarksAsync));
        }

        sources.Add(("github", NewStarsAsync));

        // Oldest first: a run that dies midway leaves a stored prefix, so the next run's newest-first scan still reaches the rest.
        var failed = new List<Exception>();
        foreach (var (name, fetch) in sources)
        {
            try
            {
                fresh.AddRange((await fetch(ct)).AsEnumerable().Reverse());
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                Logger.LogWarning(ex, "Signals from {Source} unavailable; the next daily run catches up", name);
                failed.Add(ex);
            }
        }

        if (failed.Count == sources.Count)
        {
            throw new AggregateException("Every signal source failed", failed);
        }

        foreach (var chunk in fresh.Chunk(Settings.Llm.EmbedBatch))
        {
            var vectors = await llm.EmbedAsync(chunk.Select(c => c.Text).ToList(), "signal", ct);
            for (var i = 0; i < chunk.Length; i++)
            {
                await signals.AddAsync(chunk[i].Signal, vectors[i], ct);
            }
        }

        return fresh.Count;
    }

    /// <summary>Newest first until a stored one; notes and images have no URL and are skipped.</summary>
    private async Task<List<(NewSignal, string)>> NewBookmarksAsync(CancellationToken ct)
    {
        var found = new List<(NewSignal, string)>();
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var (items, next) = await karakeep.PageAsync(cursor, ct);
            foreach (var b in items)
            {
                if (await signals.ExistsAsync("karakeep", b.Id, ct))
                {
                    return found;
                }

                if (b.Url is not null)
                {
                    found.Add((new NewSignal("karakeep", b.Id, b.Url, b.Title, b.CreatedAt.UtcDateTime), $"{b.Title}\n{b.Description}".Trim()));
                }
            }

            if (next is null)
            {
                return found;
            }

            cursor = next;
        }

        return found;
    }

    private async Task<List<(NewSignal, string)>> NewStarsAsync(CancellationToken ct)
    {
        var found = new List<(NewSignal, string)>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var stars = await github.PageAsync(page, ct);
            foreach (var s in stars)
            {
                if (await signals.ExistsAsync("github_star", s.FullName, ct))
                {
                    return found;
                }

                found.Add((new NewSignal("github_star", s.FullName, s.Url, s.FullName, s.StarredAt.UtcDateTime),
                    $"{s.FullName}: {s.Description} {string.Join(' ', s.Topics)}".Trim()));
            }

            if (stars.Count < 100)
            {
                return found;
            }
        }

        return found;
    }
}
