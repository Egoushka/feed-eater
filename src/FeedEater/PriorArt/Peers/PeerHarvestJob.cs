using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Switchknife.Facts;
using FeedEater.Llm;
using FeedEater.Signals;

namespace FeedEater.PriorArt.Peers;

public sealed record PeerHarvestReport(string Repo, bool Fingerprinted, int Packages, bool Carded, IReadOnlyList<string> Skipped);

/// <summary>
/// One repo at a time: a <c>fingerprint</c> episode (dependencies, the existing snapshot facts, disk usage; no model) and a
/// <c>card</c> episode (addresses, has_feature, uses from its README and file tree; one cheap call). Both are idempotent
/// episodes, so a run cut short just resumes: an unchanged repo stores nothing and spends no budget. No schedule: run on
/// demand from the CLI or MCP.
/// </summary>
public sealed class PeerHarvestJob(
    PriorArtStore store, GitHubStarsClient stars, PeerGitHubClient github, PeerSetClient peerSet, LiteLlmClient llm, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    public async Task<IReadOnlyList<PeerHarvestReport>> HarvestSetAsync(CancellationToken ct)
    {
        var text = await peerSet.TextAsync(ct) ?? throw new InvalidOperationException("PeerHarvest:SetUrl is not set.");
        var set = PeerSetParser.Parse(text);
        var reports = new List<PeerHarvestReport>();
        foreach (var repo in set.All)
        {
            reports.Add(await HarvestAsync(repo, ct));
        }

        return reports;
    }

    public async Task<PeerHarvestReport> HarvestAsync(string repo, CancellationToken ct)
    {
        await store.EnsureReadyAsync(ct);
        var (owner, name) = Split(repo);
        var skipped = new List<string>();
        var facts = await stars.RepoFactsAsync(owner, name, ct);
        if (facts is null)
        {
            skipped.Add("GitHub has no repo facts for it (missing, rate limited, or no access)");
        }

        var (fingerprinted, packages) = await FingerprintAsync(repo, owner, name, facts, ct);
        var carded = await CardAsync(repo, owner, name, ct, skipped);
        return new PeerHarvestReport(repo, fingerprinted, packages, carded, skipped);
    }

    private async Task<(bool Changed, int Packages)> FingerprintAsync(string repo, string owner, string name, RepoFacts? facts, CancellationToken ct)
    {
        var packages = facts is null ? null : await github.SbomAsync(owner, name, ct);
        var now = time.GetUtcNow();
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newFacts = new List<NewFact>();
        if (packages is { Count: > 0 })
        {
            var tool = await Tool(repo, ct);
            foreach (var p in packages)
            {
                var package = await store.PackageAsync(p.Name, p.Purl, ct);
                var text = p.Version is null ? $"{repo} depends on {p.Name}." : $"{repo} depends on {p.Name} {p.Version}.";
                newFacts.Add(new NewFact(tool, PriorArtSchema.DependsOn, null, now, Text: PriorArtText.Cut(text), LocatorJson: Locator(repo, day), ObjectEntity: package));
            }
        }

        if (facts is not null)
        {
            var tool = await Tool(repo, ct);
            newFacts.Add(new NewFact(tool, "stars", facts.Stars.ToString(CultureInfo.InvariantCulture), now, Text: $"{repo} had {facts.Stars:N0} stars on {day}.", LocatorJson: Locator(repo, day)));
            if (facts.PushedAt is { } pushed)
            {
                newFacts.Add(new NewFact(
                    tool, "last_push", pushed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), now,
                    Text: $"{repo} was last pushed to on {pushed:yyyy-MM-dd} (seen {day}).", LocatorJson: Locator(repo, day)));
            }

            newFacts.Add(new NewFact(
                tool, PriorArtSchema.DiskUsage, facts.DiskUsageKb.ToString(CultureInfo.InvariantCulture), now,
                Text: $"{repo} is {facts.DiskUsageKb:N0} KB on disk (seen {day}).", LocatorJson: Locator(repo, day)));
        }

        if (newFacts.Count == 0)
        {
            return (false, 0);
        }

        var hash = PriorArtText.Sha256(string.Join('\n', (packages ?? []).Select(p => p.Version is null ? p.Purl : $"{p.Purl}@{p.Version}")) + "\n" + (facts?.ReleaseTag ?? ""));
        var episode = new NewEpisode(PriorArtSchema.SourceFingerprint, repo, hash, now, PriorArtSchema.TrustOf(PriorArtSchema.SourceFingerprint));
        var result = await store.Facts.IngestAsync(episode, newFacts, ct);
        return (!result.Duplicate, packages?.Count ?? 0);
    }

    private async Task<bool> CardAsync(string repo, string owner, string name, CancellationToken ct, List<string> skipped)
    {
        PeerReadme? readme;
        try
        {
            readme = await github.ReadmeAsync(owner, name, ct);
        }
        catch (HttpRequestException e)
        {
            skipped.Add($"README unavailable: {e.Message}");
            return false;
        }

        if (readme is null)
        {
            skipped.Add("no README");
            return false;
        }

        if (await store.HasEpisodeAsync(PriorArtSchema.SourceCard, repo, readme.Sha, ct))
        {
            return false;   // unchanged since the last card: the model is not called again
        }

        IReadOnlyList<string> tree;
        try
        {
            tree = await github.TreeAsync(owner, name, ct);
        }
        catch (HttpRequestException)
        {
            tree = [];
        }

        var settings = options.Value.PeerHarvest;
        PeerCardResult card;
        try
        {
            var reply = await llm.ChatAsync(settings.CardModel, PeerCard.System, PeerCard.User(repo, readme.Text, tree), settings.CardMaxTokens, "peer-harvest-card", ct);
            card = PeerCard.Parse(reply.Content);
        }
        catch (Exception e) when (e is HttpRequestException or BudgetExceededException)
        {
            skipped.Add($"card: {e.Message}");
            return false;
        }

        var now = time.GetUtcNow();
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tool = await Tool(repo, ct);
        var newFacts = new List<NewFact>();
        foreach (var c in card.Addresses)
        {
            var problem = await store.ProblemAsync(c.Argument, ct);
            newFacts.Add(new NewFact(tool, "addresses", null, now, Text: PriorArtText.Cut(WithMarketing(c)), LocatorJson: CardLocator(readme, day, c.Marketing), ObjectEntity: problem));
        }

        foreach (var c in card.Features)
        {
            var feature = await store.FeatureAsync(c.Argument, ct);
            newFacts.Add(new NewFact(tool, PriorArtSchema.HasFeature, null, now, Text: PriorArtText.Cut(WithMarketing(c)), LocatorJson: CardLocator(readme, day, c.Marketing), ObjectEntity: feature));
        }

        foreach (var c in card.Uses)
        {
            var approach = await store.ApproachAsync(c.Argument, ct);
            newFacts.Add(new NewFact(tool, "uses", null, now, Text: PriorArtText.Cut(WithMarketing(c)), LocatorJson: CardLocator(readme, day, c.Marketing), ObjectEntity: approach));
        }

        var episode = new NewEpisode(PriorArtSchema.SourceCard, repo, readme.Sha, now, PriorArtSchema.TrustOf(PriorArtSchema.SourceCard));
        var result = await store.Facts.IngestAsync(episode, newFacts, ct);
        return !result.Duplicate;
    }

    private async Task<Guid> Tool(string repo, CancellationToken ct) => await store.ToolAsync(repo[(repo.LastIndexOf('/') + 1)..], "gh:" + repo, ct);

    private static string WithMarketing(CardClaim c) => c.Marketing ? $"{c.Text} [marketing]" : c.Text;

    private static (string Owner, string Name) Split(string repo)
    {
        var slash = repo.IndexOf('/');
        return slash > 0 ? (repo[..slash], repo[(slash + 1)..]) : throw new ArgumentException($"'{repo}' is not owner/name.", nameof(repo));
    }

    private static string Locator(string repo, string day) => Json(("repo", repo), ("retrieved", day), ("confidence", 1.0));

    private static string CardLocator(PeerReadme readme, string day, bool marketing) =>
        Json(("url", readme.Url), ("retrieved", day), ("label", marketing ? "marketing" : null), ("confidence", marketing ? 0.3 : 0.6));

    private static string Json(params (string Key, object? Value)[] fields) => JsonSerializer.Serialize(fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value));
}
