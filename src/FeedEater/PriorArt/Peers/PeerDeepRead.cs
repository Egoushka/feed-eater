using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Switchknife.Facts;

namespace FeedEater.PriorArt.Peers;

/// <summary>How a harvest run reads: <paramref name="Deep"/> off skips chargehand, <paramref name="Force"/> reads again whatever the release.</summary>
public sealed record HarvestMode(bool Deep = true, bool Force = false);

/// <summary>What one set run has spent on deep reads. A read whose cost chargehand could not give counts as its full budget.</summary>
public sealed class SpendGuard(double cap)
{
    public double Spent { get; private set; }

    public bool CanStart => Spent < cap;

    public void Add(double? usd, double budget) => Spent += usd ?? budget;
}

public sealed record DeepReadResult(bool Read, int Facts, double Usd, MergeOutcome? Merges, string? Skipped)
{
    public static DeepReadResult Skip(string reason) => new(false, 0, 0, null, reason);

    public static readonly DeepReadResult Unchanged = new(false, 0, 0, null, null);
}

/// <summary>
/// The deep read of one repo: asks chargehand when <see cref="ReadPolicy"/> says so, stores the <c>result/v1</c> as a <c>repo-read</c>
/// episode through the importer, notes the release it read, then matches the repo's features against the others'.
/// </summary>
public sealed class PeerDeepRead(PriorArtStore store, PriorArtImporter importer, IRepoReader reader, FeatureResolver resolver, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    public async Task<DeepReadResult> ReadAsync(string repo, string? latestRelease, string template, IReadOnlyList<string> aspects, HarvestMode mode, SpendGuard spend, CancellationToken ct)
    {
        var settings = options.Value.PeerHarvest;
        if (!mode.Deep)
        {
            return DeepReadResult.Unchanged;
        }

        if (!settings.DeepEnabled)
        {
            return DeepReadResult.Skip("deep read is off: PeerHarvest:ChargehandUrl is not set");
        }

        await store.EnsureReadyAsync(ct);
        var toolRef = "gh:" + repo;
        var hasRead = await store.HasSourceAsync(PriorArtSchema.SourceRepoRead, $"{toolRef}#{template}", ct);
        var lastRelease = await store.LatestReleaseAsync(toolRef, ct);
        if (!ReadPolicy.ShouldRead(hasRead, lastRelease, latestRelease, mode.Force))
        {
            return DeepReadResult.Unchanged;
        }

        if (!spend.CanStart)
        {
            return DeepReadResult.Skip($"spend cap of ${settings.SetSpendCapUsd.ToString("0.00", CultureInfo.InvariantCulture)} for this run reached");
        }

        RepoRead read;
        try
        {
            read = await reader.ReadAsync(toolRef, PeerQuestion.Build(template, aspects), ct);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            spend.Add(null, settings.DeepBudgetUsd);   // a failed run may still have been charged
            return DeepReadResult.Skip($"deep read failed: {e.Message}");
        }

        spend.Add(read.Usd, settings.DeepBudgetUsd);
        var usd = read.Usd ?? settings.DeepBudgetUsd;
        ImportReport report;
        try
        {
            report = await importer.ImportAsync(new ImportRequest(ImportFormat.ResultV1, read.ResultJson, Tool: toolRef, Problem: template), ct);
        }
        catch (PriorArtInputException e)
        {
            return new DeepReadResult(true, 0, usd, null, $"read not stored: {e.Message}");
        }

        await NoteReleaseAsync(repo, toolRef, latestRelease, ct);
        var merges = report.Duplicate ? null : await resolver.ResolveAsync(repo, ct);
        return new DeepReadResult(true, report.Duplicate ? 0 : report.Facts, usd, merges, null);
    }

    /// <summary>Remembers the release the read happened at; <see cref="ReadPolicy"/> compares the next release with it.</summary>
    private async Task NoteReleaseAsync(string repo, string toolRef, string? release, CancellationToken ct)
    {
        if (release is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var tool = await store.ToolAsync(repo[(repo.LastIndexOf('/') + 1)..], toolRef, ct);
        var locator = JsonSerializer.Serialize(new Dictionary<string, object> { ["repo"] = repo, ["confidence"] = 1.0 });
        var episode = new NewEpisode(PriorArtSchema.SourceFingerprint, $"{repo}#read-release", release, now, PriorArtSchema.TrustOf(PriorArtSchema.SourceFingerprint));
        await store.Facts.IngestAsync(episode, [new NewFact(tool, "latest_release", release, now, Text: $"{repo} was last read at release {release}.", LocatorJson: locator)], ct);
    }
}
