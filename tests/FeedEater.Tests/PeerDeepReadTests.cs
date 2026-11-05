using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.PriorArt;
using FeedEater.PriorArt.Peers;

namespace FeedEater.Tests;

internal sealed class FakeReader(Func<string, RepoRead> answer) : IRepoReader
{
    public List<string> Repos { get; } = [];

    public Task<RepoRead> ReadAsync(string repositoryPath, string question, CancellationToken ct)
    {
        Repos.Add(repositoryPath);
        return Task.FromResult(answer(repositoryPath));
    }
}

internal sealed class FakeJudge(Func<string, string, bool> same) : IFeatureJudge
{
    public List<(string A, string B)> Asked { get; } = [];

    public Task<bool> SameAsync(string a, string b, CancellationToken ct)
    {
        Asked.Add((a, b));
        return Task.FromResult(same(a, b));
    }
}

internal sealed class FakeFiles(Dictionary<string, int> lineCounts) : IFileLines
{
    public Task<int?> LineCountAsync(string owner, string repo, string path, string sha, CancellationToken ct) =>
        Task.FromResult(lineCounts.TryGetValue($"{owner}/{repo}:{path}@{sha}", out var n) ? (int?)n : null);
}

/// <summary>Checks 1, 2, 3 (known_at), 5, 6 and 7 of docs/specs/2026-11-05-peer-deep-read.md, against a real Postgres.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PeerDeepReadTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(Start);
    private readonly HashEmbedder embedder = new();
    private PriorArtStore store = null!;
    private PriorArtImporter importer = null!;
    private IOptions<FeedEaterOptions> options = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        store = new PriorArtStore(pg.Db, time);
        importer = new PriorArtImporter(store, embedder, time);
        options = Options.Create(new FeedEaterOptions { PeerHarvest = new PeerHarvestOptions { ChargehandUrl = "https://chargehand.test", SetSpendCapUsd = 5, DeepBudgetUsd = 0.5 } });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Result(string[] claims, string commit = "abcdef0123456789", string usage = """{"input":1,"output":1,"cache_read":0,"cache_write":0,"usd":0.01}""") =>
        $$"""
        {"contract_version":"result/v1","task_id":"t-1","node_id":"n-1","trace_id":"0af7651916cd43dd8448eb211c80319c","status":"completed",
         "summary":"s","claims":[{{string.Join(",", claims.Select((c, i) => Claim(c, i)))}}],
         "evidence":[{{string.Join(",", claims.Select((_, i) => File(i, commit)))}}],"artifacts":[],"open_questions":[],"confidence":0.8,"usage":{{usage}}}
        """;

    private static string File(int i, string commit) => $$"""{"id":"e{{i}}","kind":"file","locator":"src/f{{i}}.cs:1-{{10 + i}}","commit":"{{commit}}"}""";

    private static string Claim(string text, int i) => JsonSerializer.Serialize(new Dictionary<string, object> { ["text"] = text, ["evidence"] = new[] { $"e{i}" }, ["confidence"] = 0.9 });

    private Task<ImportReport> ReadInto(string repo, params string[] claims) =>
        importer.ImportAsync(new ImportRequest(ImportFormat.ResultV1, Result(claims), Tool: "gh:" + repo, Problem: "peer-template-v1"), CancellationToken.None);

    private FeatureResolver Resolver(FakeJudge judge) => new(store, embedder, judge, options, time);

    private PriorArtGaps Gaps() => new(store, new PeerSetClient(new HttpClient(), options), time);

    private async Task<int> CountAsync(string sql, object? args = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>(sql, args);
    }

    // --- check 1 ---

    [Fact]
    public async Task A_feature_claim_becomes_a_has_feature_fact_on_the_feature_entity_and_other_tags_still_work()
    {
        var report = await ReadInto("p/a", "[feature:full-text-search] It indexes titles and bodies.", "[uses:inverted-index] It keeps an inverted index.", "[limitation] No phrase queries.");

        Assert.False(report.Duplicate);
        var rows = await store.FeatureFactsAsync(time.GetUtcNow(), CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal("gh:p/a", row.ToolRef);
        Assert.Equal(await store.FeatureAsync("full-text-search", CancellationToken.None), row.FeatureId);
        Assert.Equal("src/f0.cs", JsonDocument.Parse(row.Locator).RootElement.GetProperty("path").GetString());
        Assert.Single(await store.UsesAtAsync(time.GetUtcNow(), CancellationToken.None));
        Assert.Equal(1, await CountAsync("select count(*) from facts.facts where namespace = 'prior-art' and predicate = 'limitation'"));
    }

    [Fact]
    public async Task The_same_slug_from_two_tools_is_one_feature_entity()
    {
        await ReadInto("p/a", "[feature:full-text-search] A has it.");
        await ReadInto("p/b", "[feature:full-text-search] B has it.");

        Assert.Single(await store.FeatureEntitiesAsync(CancellationToken.None));
        Assert.Equal(2, (await store.FeatureFactsAsync(time.GetUtcNow(), CancellationToken.None)).Count);
    }

    // --- check 2 ---

    private async Task SeedForMergeAsync()
    {
        await ReadInto("me/own", "[feature:full-text-search] Own search.", "[feature:retry-with-backoff] Own retry.");
        await ReadInto("p/a", "[feature:full-text-search-engine] A search.", "[feature:retry-with-exponential-backoff] A retry.");
        await ReadInto("p/b", "[feature:vector-index] B index.");
    }

    [Fact]
    public async Task A_confirmed_pair_is_merged_with_an_alias_and_a_same_as_fact_and_a_rejected_pair_is_recorded_as_distinct()
    {
        await SeedForMergeAsync();
        var judge = new FakeJudge((a, b) => (a + b).Contains("search", StringComparison.Ordinal));

        var outcome = await Resolver(judge).ResolveAsync("me/own", CancellationToken.None);

        Assert.Equal(1, outcome.Merged);
        Assert.Equal(1, outcome.Rejected);
        var entities = (await store.FeatureEntitiesAsync(CancellationToken.None)).ToDictionary(e => e.ExternalRef!);
        Assert.Equal(entities["feature:full-text-search"].Id, entities["feature:full-text-search-engine"].MergedInto);   // tie on tool count: the name that sorts first survives
        Assert.Null(entities["feature:full-text-search"].MergedInto);
        Assert.Null(entities["feature:retry-with-backoff"].MergedInto);
        Assert.Null(entities["feature:retry-with-exponential-backoff"].MergedInto);
        var merge = Assert.Single(await store.MergesAsync(CancellationToken.None));
        Assert.Equal("full text search engine", merge.LoserName);
        Assert.Equal(1, await CountAsync("select count(*) from facts.entity_aliases where entity_id = @id and alias = 'full text search engine'", new { id = entities["feature:full-text-search"].Id }));
        Assert.Equal(1, await CountAsync("select count(*) from facts.facts where namespace = 'prior-art' and predicate = 'distinct_from' and retired_at is null"));
    }

    [Fact]
    public async Task A_decided_pair_is_not_asked_again()
    {
        await SeedForMergeAsync();
        var judge = new FakeJudge((a, b) => (a + b).Contains("search", StringComparison.Ordinal));
        await Resolver(judge).ResolveAsync("me/own", CancellationToken.None);
        var asked = judge.Asked.Count;

        var again = await Resolver(judge).ResolveAsync("me/own", CancellationToken.None);

        Assert.Equal(asked, judge.Asked.Count);
        Assert.Equal(0, again.Compared);
    }

    [Fact]
    public async Task Undo_restores_the_feature_and_it_is_not_merged_again()
    {
        await SeedForMergeAsync();
        var judge = new FakeJudge((a, b) => (a + b).Contains("search", StringComparison.Ordinal));
        var resolver = Resolver(judge);
        await resolver.ResolveAsync("me/own", CancellationToken.None);
        var merge = Assert.Single(await store.MergesAsync(CancellationToken.None));
        var asked = judge.Asked.Count;

        Assert.True(await resolver.UndoAsync(merge.FactId, CancellationToken.None));

        Assert.Empty(await store.MergesAsync(CancellationToken.None));
        Assert.All(await store.FeatureEntitiesAsync(CancellationToken.None), e => Assert.Null(e.MergedInto));
        Assert.Equal(0, await CountAsync("select count(*) from facts.entity_aliases where alias = 'full text search engine'"));
        await resolver.ResolveAsync("me/own", CancellationToken.None);
        Assert.Equal(asked, judge.Asked.Count);
        Assert.Empty(await store.MergesAsync(CancellationToken.None));
        Assert.False(await resolver.UndoAsync(merge.FactId, CancellationToken.None));   // already undone
    }

    [Fact]
    public async Task Names_below_the_similarity_threshold_are_never_sent_to_the_judge()
    {
        await ReadInto("me/own", "[feature:full-text-search] Own search.");
        await ReadInto("p/b", "[feature:vector-index] B index.");
        var judge = new FakeJudge((_, _) => true);

        var outcome = await Resolver(judge).ResolveAsync("me/own", CancellationToken.None);

        Assert.Empty(judge.Asked);
        Assert.Equal(0, outcome.Compared);
    }

    // --- check 3: known_at and merged features through the service ---

    [Fact]
    public async Task Gaps_known_at_an_earlier_time_do_not_see_later_facts()
    {
        await ReadInto("me/own", "[feature:retry-with-backoff] Own retry.");
        await ReadInto("p/a", "[feature:full-text-search] A search.");
        var early = time.GetUtcNow();
        time.Advance(TimeSpan.FromDays(2));
        await ReadInto("p/b", "[feature:vector-search] B search.");

        var before = await Gaps().QueryAsync("me/own", null, 1, early.AddHours(1), CancellationToken.None);
        var after = await Gaps().QueryAsync("me/own", null, 1, null, CancellationToken.None);

        Assert.Equal(["full text search"], before.Gaps.Select(g => g.Feature).ToArray());
        Assert.Equal(["full text search", "vector search"], after.Gaps.Select(g => g.Feature).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_merged_feature_is_one_gap_row_with_both_peers()
    {
        await ReadInto("me/own", "[feature:retry-with-backoff] Own retry.");
        await ReadInto("p/b", "[feature:full-text-search-engine] B search.");
        await ReadInto("p/a", "[feature:full-text-search] A search.");
        await Resolver(new FakeJudge((_, _) => true)).ResolveAsync("p/a", CancellationToken.None);   // the read of p/a matches its names against p/b's

        var report = await Gaps().QueryAsync("me/own", null, 2, null, CancellationToken.None);

        var gap = Assert.Single(report.Gaps);
        Assert.Equal("full text search", gap.Feature);
        Assert.Equal(2, gap.Peers.Count);
    }

    // --- checks 4, 5, 6: the deep read of one repo ---

    private PeerDeepRead DeepRead(FakeReader reader, FakeJudge? judge = null) =>
        new(store, importer, reader, Resolver(judge ?? new FakeJudge((_, _) => false)), options, time);

    private static string Read(string feature, string commit = "abcdef0123456789") => Result([$"[feature:{feature}] The code has {feature.Replace('-', ' ')}."], commit);

    [Fact]
    public async Task A_rerun_with_no_new_release_does_not_call_chargehand_and_adds_no_episode()
    {
        var reader = new FakeReader(_ => new RepoRead(Read("full-text-search"), 0.2));
        var deep = DeepRead(reader);
        var mode = new HarvestMode();

        var first = await deep.ReadAsync("p/a", "v1.2.0", "peer-template-v1", [], mode, new SpendGuard(5), CancellationToken.None);
        var episodes = await CountAsync("select count(*) from facts.episodes where namespace = 'prior-art'");
        time.Advance(TimeSpan.FromDays(7));
        var second = await deep.ReadAsync("p/a", "v1.2.0", "peer-template-v1", [], mode, new SpendGuard(5), CancellationToken.None);
        var patch = await deep.ReadAsync("p/a", "v1.2.4", "peer-template-v1", [], mode, new SpendGuard(5), CancellationToken.None);

        Assert.True(first.Read);
        Assert.False(second.Read);
        Assert.False(patch.Read);
        Assert.Single(reader.Repos);
        Assert.Equal("gh:p/a", reader.Repos[0]);
        Assert.Equal(episodes, await CountAsync("select count(*) from facts.episodes where namespace = 'prior-art'"));
        Assert.Equal("v1.2.0", await store.LatestReleaseAsync("gh:p/a", CancellationToken.None));
    }

    [Fact]
    public async Task A_new_minor_release_reads_again_and_the_new_version_replaces_the_old_claims()
    {
        var reads = 0;
        var reader = new FakeReader(_ => new RepoRead(++reads == 1 ? Read("full-text-search") : Read("vector-search", "fedcba9876543210"), 0.2));
        var deep = DeepRead(reader);
        await deep.ReadAsync("p/a", "v1.2.0", "peer-template-v1", [], new HarvestMode(), new SpendGuard(5), CancellationToken.None);
        time.Advance(TimeSpan.FromDays(30));

        var second = await deep.ReadAsync("p/a", "v1.3.0", "peer-template-v1", [], new HarvestMode(), new SpendGuard(5), CancellationToken.None);

        Assert.True(second.Read);
        Assert.Equal(2, reader.Repos.Count);
        Assert.Equal("v1.3.0", await store.LatestReleaseAsync("gh:p/a", CancellationToken.None));
        var live = await store.FeatureFactsAsync(time.GetUtcNow(), CancellationToken.None);
        Assert.Equal("vector search", await FeatureNameAsync(Assert.Single(live).FeatureId));   // the new version retired the old claim
    }

    private async Task<string> FeatureNameAsync(Guid id) => (await store.FeatureEntitiesAsync(CancellationToken.None)).Single(e => e.Id == id).Name;

    [Fact]
    public async Task Force_reads_again_whatever_the_release_and_no_deep_skips_the_call()
    {
        var reader = new FakeReader(_ => new RepoRead(Read("full-text-search"), 0.2));
        var deep = DeepRead(reader);
        await deep.ReadAsync("p/a", "v1.2.0", "peer-template-v1", [], new HarvestMode(), new SpendGuard(5), CancellationToken.None);

        var forced = await deep.ReadAsync("p/a", "v1.2.0", "peer-template-v1", [], new HarvestMode(Force: true), new SpendGuard(5), CancellationToken.None);
        var off = await deep.ReadAsync("p/z", "v1.0.0", "peer-template-v1", [], new HarvestMode(Deep: false), new SpendGuard(5), CancellationToken.None);

        Assert.True(forced.Read);
        Assert.True(forced.Facts == 0, "an identical result is a duplicate and writes nothing");
        Assert.False(off.Read);
        Assert.Equal(2, reader.Repos.Count);
    }

    [Fact]
    public async Task A_set_run_stops_starting_reads_at_the_spend_cap()
    {
        var reader = new FakeReader(repo => new RepoRead(Read("feature-" + repo[^1]), 2.5));
        var deep = DeepRead(reader);
        var spend = new SpendGuard(5);

        var results = new List<DeepReadResult>();
        foreach (var repo in new[] { "p/a", "p/b", "p/c" })
        {
            results.Add(await deep.ReadAsync(repo, "v1.0.0", "peer-template-v1", [], new HarvestMode(), spend, CancellationToken.None));
        }

        Assert.True(results[0].Read);
        Assert.True(results[1].Read);
        Assert.False(results[2].Read);
        Assert.Contains("spend cap", results[2].Skipped);
        Assert.Equal(2, reader.Repos.Count);
    }

    [Fact]
    public async Task A_read_chargehand_could_not_price_counts_as_its_full_budget()
    {
        var reader = new FakeReader(repo => new RepoRead(Read("feature-" + repo[^1]), null));
        var deep = DeepRead(reader);
        var spend = new SpendGuard(0.5);

        var first = await deep.ReadAsync("p/a", "v1.0.0", "peer-template-v1", [], new HarvestMode(), spend, CancellationToken.None);
        var second = await deep.ReadAsync("p/b", "v1.0.0", "peer-template-v1", [], new HarvestMode(), spend, CancellationToken.None);

        Assert.Equal(0.5, first.Usd);
        Assert.False(second.Read);
    }

    [Fact]
    public async Task Deep_read_is_off_without_a_chargehand_url()
    {
        options = Options.Create(new FeedEaterOptions());
        var reader = new FakeReader(_ => throw new InvalidOperationException("must not be called"));

        var result = await DeepRead(reader).ReadAsync("p/a", "v1.0.0", "peer-template-v1", [], new HarvestMode(), new SpendGuard(5), CancellationToken.None);

        Assert.False(result.Read);
        Assert.Contains("ChargehandUrl", result.Skipped);
        Assert.Empty(reader.Repos);
    }

    [Fact]
    public async Task A_failed_read_is_a_skip_that_still_costs_its_budget()
    {
        var reader = new FakeReader(_ => throw new HttpRequestException("chargehand answered 502"));
        var spend = new SpendGuard(5);

        var result = await DeepRead(reader).ReadAsync("p/a", null, "peer-template-v1", [], new HarvestMode(), spend, CancellationToken.None);

        Assert.False(result.Read);
        Assert.Contains("502", result.Skipped);
        Assert.Equal(0.5, spend.Spent);
    }

    // --- check 7: the verify sampler ---

    [Fact]
    public async Task Verify_resolves_real_files_and_flags_a_missing_path_a_bad_ref_and_a_line_past_the_end()
    {
        await importer.ImportAsync(new ImportRequest(ImportFormat.ResultV1, Result(
        [
            "[feature:good-one] ok", "[feature:missing-path] gone", "[feature:bad-ref] ref", "[feature:past-the-end] long",
        ]), Tool: "gh:p/a", Problem: "peer-template-v1"), CancellationToken.None);
        // evidence i cites src/f{i}.cs:1-{10+i} at abcdef0123456789
        var files = new FakeFiles(new Dictionary<string, int>
        {
            ["p/a:src/f0.cs@abcdef0123456789"] = 50,    // lines 1-10 fit
            ["p/a:src/f3.cs@abcdef0123456789"] = 5,     // lines 1-13 do not
            // f1 has no entry: no such path; f2 likewise stands for a ref that does not exist
        });

        var report = await new PriorArtVerify(store, files).SampleAsync(10, CancellationToken.None);

        Assert.Equal(4, report.Sampled);
        Assert.Equal(1, report.Resolved);
        Assert.Equal(3, report.Failures.Count);
        Assert.Contains(report.Failures, f => f.Path == "src/f3.cs" && f.Why.Contains("5 lines", StringComparison.Ordinal));
        Assert.Contains(report.Failures, f => f.Path == "src/f1.cs" && f.Why.Contains("no such file", StringComparison.Ordinal));
    }
}
