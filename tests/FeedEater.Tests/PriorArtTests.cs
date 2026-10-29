using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.PriorArt;
using Switchknife.Facts;

namespace FeedEater.Tests;

/// <summary>Embeds words into a hashed bag, so related sentences are close and tests need no gateway.</summary>
internal sealed class HashEmbedder : IPriorArtEmbedder
{
    private const int Dimensions = 256;

    public string Model => "test-hash";

    public bool Down { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (Down)
        {
            throw new HttpRequestException("embedder down");
        }

        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(Embed).ToList());
    }

    private static float[] Embed(string text)
    {
        var v = new float[Dimensions];
        foreach (var word in text.ToLowerInvariant().Split([' ', ',', '.', ':', ';', '(', ')', '[', ']', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var h = 2166136261u;
            foreach (var ch in word)
            {
                h = (h ^ ch) * 16777619u;
            }

            v[h % Dimensions] += (h >> 16 & 1) == 0 ? 1 : -1;
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PriorArtTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(Start);
    private readonly HashEmbedder embedder = new();
    private PriorArtStore store = null!;
    private PriorArtImporter importer = null!;
    private PriorArtSearch search = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        store = new PriorArtStore(pg.Db, time);
        importer = new PriorArtImporter(store, embedder, time);
        search = new PriorArtSearch(store, embedder, Options.Create(new FeedEaterOptions()), time);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Result(string claims, string evidence, string status = "completed") =>
        """
        {"contract_version":"result/v1","task_id":"t-1","node_id":"n-1","trace_id":"0af7651916cd43dd8448eb211c80319c","status":"@STATUS@",
         "summary":"s","claims":[@CLAIMS@],"evidence":[@EVIDENCE@],"artifacts":[],"open_questions":[],"confidence":0.8,
         "usage":{"input":1,"output":1,"cache_read":0,"cache_write":0,"usd":0.01}}
        """.Replace("@STATUS@", status).Replace("@CLAIMS@", claims).Replace("@EVIDENCE@", evidence);

    private static string File(string id, string path, string commit) => $$"""{"id":"{{id}}","kind":"file","locator":"{{path}}","commit":"{{commit}}"}""";

    private static string Claim(string text, string evidenceId, double confidence = 0.9, string? support = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["text"] = text,
            ["evidence"] = new[] { evidenceId },
            ["confidence"] = confidence,
            ["support"] = support,
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

    private Task<ImportReport> ImportResultAsync(string json, string problem = "stuck loop detection") =>
        importer.ImportAsync(new ImportRequest(ImportFormat.ResultV1, json, Tool: "example/loopguard", Problem: problem), CancellationToken.None);

    private async Task<List<(string Predicate, string Text, string? Reason)>> RowsAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<(string, string, string?)>("select predicate, text, retired_reason from facts.facts where namespace = 'prior-art' order by recorded_at, text")).ToList();
    }

    [Fact]
    public async Task Predicates_are_defined_at_startup_use()
    {
        await store.EnsureReadyAsync(CancellationToken.None);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.True(await c.ExecuteScalarAsync<int>("select count(*) from facts.predicates where namespace = 'prior-art'") >= 12);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from pg_extension where extname = 'btree_gist'"));
    }

    [Fact]
    public async Task Import_result_v1_keeps_file_commit_and_lines()
    {
        var json = Result(
            Claim("[uses:repeat-call-window] The guard compares the last 8 tool calls.", "e1") + "," + Claim("[limitation] It never looks at tool arguments.", "e2", 0.8, "partial"),
            File("e1", "src/guard.py:10-42", "abcdef0123456789") + "," + File("e2", "src/guard.py:77", "abcdef0123456789"));

        var report = await ImportResultAsync(json);

        Assert.Equal(3, report.Written);   // uses, approach_for, limitation
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<Guid>("select id from facts.facts where namespace = 'prior-art' and predicate = 'uses'");
        var source = Assert.Single(await store.SourcesAsync([id], CancellationToken.None));
        using var locator = JsonDocument.Parse(source.Locator);
        Assert.Equal("example/loopguard", locator.RootElement.GetProperty("repo").GetString());
        Assert.Equal("abcdef0123456789", locator.RootElement.GetProperty("commit").GetString());
        Assert.Equal("src/guard.py", locator.RootElement.GetProperty("path").GetString());
        Assert.Equal("10-42", locator.RootElement.GetProperty("lines").GetString());
        Assert.Equal("t-1", locator.RootElement.GetProperty("run").GetString());
        Assert.Equal("agent", source.Trust);
        Assert.Equal("repo-read", source.SourceKind);
        Assert.Equal("gh:example/loopguard#stuck-loop-detection", source.SourceRef);
        Assert.Equal(0.9, locator.RootElement.GetProperty("confidence").GetDouble(), 3);
        var limitation = Assert.Single(await store.SourcesAsync([await c.ExecuteScalarAsync<Guid>("select id from facts.facts where predicate = 'limitation'")], CancellationToken.None));
        Assert.Equal(0.4, PriorArtStore.ConfidenceOf(limitation.Locator)!.Value, 3);   // 0.8, halved: support was partial
    }

    [Fact]
    public async Task Import_same_commit_twice_is_duplicate()
    {
        var json = Result(Claim("[uses:window] Compares recent calls.", "e1"), File("e1", "a.py:1-2", "abcdef0"));

        var first = await ImportResultAsync(json);
        var second = await ImportResultAsync(json);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(0, second.Written);
        Assert.Equal(2, (await RowsAsync()).Count);
    }

    [Fact]
    public async Task Import_new_commit_retires_unsupported_claim_as_source_changed()
    {
        await ImportResultAsync(Result(
            Claim("[uses:window] Compares recent calls.", "e1") + "," + Claim("[limitation] Ignores arguments.", "e2"),
            File("e1", "a.py:1-2", "aaaaaaa") + "," + File("e2", "a.py:9", "aaaaaaa")));

        time.Advance(TimeSpan.FromHours(1));
        var again = await ImportResultAsync(Result(Claim("[uses:window] Compares recent calls.", "e1"), File("e1", "a.py:1-2", "bbbbbbb")));

        Assert.False(again.Duplicate);
        var rows = await RowsAsync();
        Assert.Equal("source_changed", rows.Single(r => r.Predicate == "limitation").Reason);
        Assert.Null(rows.Single(r => r.Predicate == "uses").Reason);
    }

    [Fact]
    public async Task Web_fact_cannot_retire_code_fact()
    {
        await store.EnsureReadyAsync(CancellationToken.None);
        var tool = await store.ToolAsync("loopguard", "gh:example/loopguard", CancellationToken.None);
        var from = Start.AddDays(-30);
        await store.Facts.IngestAsync(
            new NewEpisode(PriorArtSchema.SourceRepoRead, "gh:example/loopguard#status", "aaaaaaa", Trust: PriorArtSchema.TrustOf(PriorArtSchema.SourceRepoRead)),
            [new NewFact(tool, "status", "active", from, Text: "loopguard is active")]);

        time.Advance(TimeSpan.FromMinutes(5));
        var web = await store.Facts.IngestAsync(
            new NewEpisode(PriorArtSchema.SourceWebNote, "notes/loopguard.md", "bbbbbbb", Trust: PriorArtSchema.TrustOf(PriorArtSchema.SourceWebNote)),
            [new NewFact(tool, "status", "archived", Start, Text: "loopguard is archived")]);

        Assert.Equal(1, web.Outranked);
        var now = await store.Facts.AsOfAsync(tool, "status", time.GetUtcNow());
        Assert.Equal("active", Assert.Single(now).Value);
    }

    [Fact]
    public async Task Search_flags_claim_older_than_180_days_as_stale()
    {
        await ImportResultAsync(Result(Claim("[uses:window] Compares the last calls to find a stuck loop.", "e1"), File("e1", "a.py:1-2", "abcdef0")));

        var fresh = await search.SearchAsync("stuck loop", null, 10, CancellationToken.None);
        time.Advance(TimeSpan.FromDays(181));
        var old = await search.SearchAsync("stuck loop", null, 10, CancellationToken.None);

        Assert.All(fresh.Facts, f => Assert.Null(f.Stale));
        var uses = Assert.Single(old.Facts, f => f.Predicate == "uses");
        Assert.Equal("recorded_over_180_days_ago", uses.Stale?.Reason);
    }

    [Fact]
    public async Task Search_says_hit_thin_or_miss_and_a_miss_carries_a_research_request()
    {
        await importer.ImportAsync(new ImportRequest(ImportFormat.Audit, """
            | # | Candidate | What it does | Verdict |
            |---|---|---|---|
            | 1 | [a/one](https://github.com/a/one) | Admits jobs by a spend budget | **watch** |
            | 2 | [b/two](https://github.com/b/two) | Admits jobs by a capacity budget | **pilot** |
            | 3 | [c/three](https://github.com/c/three) | Admits jobs by a queue depth budget | **skip** |
            """, SourceRef: "audits/2026-10-29-a.md"), CancellationToken.None);

        var hit = await search.SearchAsync("admits jobs budget", null, 10, CancellationToken.None);
        var thin = await search.SearchAsync("spend budget", null, 10, CancellationToken.None);
        var miss = await search.SearchAsync("quantum chromodynamics lattice", null, 10, CancellationToken.None);

        Assert.Equal("hit", hit.Coverage);
        Assert.Null(hit.Research);
        Assert.Equal("thin", thin.Coverage);
        Assert.NotNull(thin.Research);
        Assert.Equal("miss", miss.Coverage);
        Assert.Equal("request/v1", miss.Research?["request"]?["contract_version"]?.GetValue<string>());
    }

    [Fact]
    public async Task Search_answers_from_words_when_the_embedder_is_down()
    {
        await importer.ImportAsync(new ImportRequest(ImportFormat.Audit, "| Candidate | What it does |\n|---|---|\n| [a/one](https://github.com/a/one) | Detects stuck loops |\n", SourceRef: "a.md"), CancellationToken.None);
        embedder.Down = true;

        var answer = await search.SearchAsync("stuck loops", null, 10, CancellationToken.None);

        Assert.Contains("words only", answer.Ranking, StringComparison.Ordinal);
        Assert.NotEmpty(answer.Facts);
    }

    [Fact]
    public async Task Search_known_at_shows_what_the_base_held_then()
    {
        await importer.ImportAsync(new ImportRequest(ImportFormat.Audit, "| Candidate | What it does |\n|---|---|\n| [a/one](https://github.com/a/one) | Detects stuck loops |\n", SourceRef: "a.md"), CancellationToken.None);

        var before = await search.SearchAsync("stuck loops", Start.AddDays(-1), 10, CancellationToken.None);
        var after = await search.SearchAsync("stuck loops", null, 10, CancellationToken.None);

        Assert.Empty(before.Facts);
        Assert.NotEmpty(after.Facts);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => search.SearchAsync("x", Start.AddDays(1), 10, CancellationToken.None));
    }

    [Fact]
    public async Task Patterns_name_approaches_that_two_tools_use()
    {
        foreach (var tool in new[] { "example/one", "example/two" })
        {
            await importer.ImportAsync(
                new ImportRequest(ImportFormat.ResultV1, Result(Claim("[uses:bi-temporal-edges] Keeps valid and recorded time on every edge.", "e1"), File("e1", "m.py:1-9", "abcdef0")),
                    Tool: tool, Problem: "temporal memory"), CancellationToken.None);
        }

        var answer = await search.SearchAsync("valid and recorded time on every edge", null, 10, CancellationToken.None);

        var pattern = Assert.Single(answer.Patterns);
        Assert.Equal("bi temporal edges", pattern.Approach);
        Assert.Equal(["one", "two"], pattern.Tools.Order().ToArray());
    }

    [Fact]
    public async Task Explain_returns_sources_and_names()
    {
        await ImportResultAsync(Result(Claim("[limitation] Ignores tool arguments.", "e1"), File("e1", "a.py:9", "abcdef0")));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<Guid>("select id from facts.facts where predicate = 'limitation'");

        var why = await new PriorArtExplain(store).ExplainAsync(id, CancellationToken.None);

        var source = Assert.Single(why!["sources"]!.AsArray());
        Assert.Equal("repo-read", source!["kind"]!.GetValue<string>());
        Assert.Equal("a.py", source["locator"]!["path"]!.GetValue<string>());
        Assert.Equal("loopguard", why["fact"]!["tool"]!.GetValue<string>());
        Assert.Null(await new PriorArtExplain(store).ExplainAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Audit_import_stores_description_evidence_and_verdict_with_confidence_and_low_confidence_when_unverified()
    {
        const string md = "| # | Repo | What it does | Evidence | Verdict |\n|---|---|---|---|---|\n| 1 | [m/tool](https://github.com/m/tool) | Tests MCP servers | 158 stars, pushed 09-14, MIT | **pilot.** Run the checks. |\n";

        var report = await importer.ImportAsync(new ImportRequest(ImportFormat.Audit, md, SourceRef: "audits/2026-10-29-sweep.md", Unverified: true), CancellationToken.None);

        Assert.Equal(3, report.Written);
        var rows = await RowsAsync();
        Assert.Contains(rows, r => r.Predicate == "assessed" && r.Text.StartsWith("m/tool assessed pilot", StringComparison.Ordinal));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<Guid>("select id from facts.facts where predicate = 'assessed'");
        var source = Assert.Single(await store.SourcesAsync([id], CancellationToken.None));
        Assert.Equal(0.3, PriorArtStore.ConfidenceOf(source.Locator)!.Value, 3);
        Assert.Equal("audit", source.SourceKind);
        Assert.False(JsonDocument.Parse(source.Locator).RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc), await c.ExecuteScalarAsync<DateTime>("select lower(valid) from facts.facts where id = @id", new { id }));
    }

    [Fact]
    public async Task Audit_edit_retires_the_row_it_dropped()
    {
        const string head = "| Candidate | What it does |\n|---|---|\n";
        await importer.ImportAsync(new ImportRequest(ImportFormat.Audit, head + "| [a/one](https://github.com/a/one) | First |\n| [b/two](https://github.com/b/two) | Second |\n", SourceRef: "a.md"), CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(1));
        await importer.ImportAsync(new ImportRequest(ImportFormat.Audit, head + "| [a/one](https://github.com/a/one) | First |\n", SourceRef: "a.md"), CancellationToken.None);

        var rows = await RowsAsync();
        Assert.Equal("source_changed", rows.Single(r => r.Text.Contains("Second", StringComparison.Ordinal)).Reason);
        Assert.Null(rows.Single(r => r.Text.Contains("First", StringComparison.Ordinal)).Reason);
    }

    [Fact]
    public async Task Notes_are_third_party_facts_with_url_and_dates()
    {
        const string notes = "### Postiz: AGPL-3.0\n- URL: https://github.com/gitroomhq/postiz-app\n- Source date: UNKNOWN\n- Retrieved: 2026-10-04\n- Fact: License AGPL-3.0.\n- Label: [marketing]\n";

        await importer.ImportAsync(new ImportRequest(ImportFormat.Notes, notes, SourceRef: "notes/a.md"), CancellationToken.None);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<Guid>("select id from facts.facts where predicate = 'statement'");
        var source = Assert.Single(await store.SourcesAsync([id], CancellationToken.None));
        Assert.Equal("third_party", source.Trust);
        using var locator = JsonDocument.Parse(source.Locator);
        Assert.Equal("UNKNOWN", locator.RootElement.GetProperty("source_date").GetString());
        Assert.Equal("marketing", locator.RootElement.GetProperty("label").GetString());
        Assert.Equal(0.3, locator.RootElement.GetProperty("confidence").GetDouble(), 3);   // 0.6, halved for marketing
        Assert.EndsWith("[marketing]", (await RowsAsync()).Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_csv_attaches_counters_to_known_tools_only_and_is_not_embedded()
    {
        await importer.ImportAsync(new ImportRequest(ImportFormat.Audit, "| Candidate | What it does |\n|---|---|\n| [m/tool](https://github.com/m/tool) | Tests things |\n", SourceRef: "a.md"), CancellationToken.None);

        var report = await importer.ImportAsync(new ImportRequest(ImportFormat.Snapshot, "repo,stars,pushed,license\nm/tool,158,2026-09-14,MIT\nm/other,5,2026-01-01,MIT\n", SourceRef: "sweep-2026-10-29.csv"), CancellationToken.None);

        Assert.Equal(3, report.Written);
        Assert.Contains("1 repositories", Assert.Single(report.Skipped), StringComparison.Ordinal);
        var stats = await store.StatsAsync(embedder.Model, CancellationToken.None);
        Assert.Equal(0, stats.Unembedded);
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from facts.fact_vectors"));   // only the statement
    }

    [Fact]
    public async Task Import_refuses_what_it_cannot_store()
    {
        async Task Refused(ImportRequest r) => await Assert.ThrowsAsync<PriorArtInputException>(() => importer.ImportAsync(r, CancellationToken.None));

        await Refused(new ImportRequest(ImportFormat.ResultV1, "not json", Tool: "a/b", Problem: "p"));
        await Refused(new ImportRequest(ImportFormat.ResultV1, Result("", "", "failed"), Tool: "a/b", Problem: "p"));
        await Refused(new ImportRequest(ImportFormat.ResultV1, Result("", ""), Tool: "a/b"));
        await Refused(new ImportRequest(ImportFormat.ResultV1, Result("", ""), Tool: "not a tool!", Problem: "p"));
        await Refused(new ImportRequest(ImportFormat.Audit, "| a | b |\n|---|---|\n| 1 | 2 |\n"));
        await Refused(new ImportRequest(ImportFormat.Audit, " ", SourceRef: "a.md"));
        await Refused(new ImportRequest(ImportFormat.Audit, "x", SourceRef: "a.md", Confidence: 2));
    }

    [Fact]
    public async Task Claim_whose_evidence_is_missing_is_skipped_not_stored()
    {
        var report = await ImportResultAsync(Result(Claim("[uses:window] Good.", "e1") + "," + Claim("[uses:window] Bad.", "nope"), File("e1", "a.py:1", "abcdef0")));

        Assert.Equal(2, report.Written);
        Assert.Contains("claim 2", Assert.Single(report.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedding_catches_up_after_an_outage()
    {
        embedder.Down = true;
        var down = await ImportResultAsync(Result(Claim("[limitation] Ignores arguments.", "e1"), File("e1", "a.py:1", "abcdef0")));
        embedder.Down = false;
        var (embedded, pending) = await importer.EmbedAsync(CancellationToken.None);

        Assert.Equal(1, down.EmbedPending);
        Assert.Equal(0, down.Embedded);
        Assert.Equal((1, 0), (embedded, pending));
    }
}
