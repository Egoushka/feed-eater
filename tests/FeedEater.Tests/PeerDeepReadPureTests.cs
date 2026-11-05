using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.PriorArt;
using FeedEater.PriorArt.Peers;

namespace FeedEater.Tests;

/// <summary>Checks 3, 4, 6 and 8 of docs/specs/2026-11-05-peer-deep-read.md that need no database.</summary>
public sealed class PeerDeepReadPureTests
{
    [Theory]
    [InlineData(true, "v1.2.0", "v1.3.0", false, true)]    // new minor
    [InlineData(true, "v1.2.0", "v2.0.0", false, true)]    // new major
    [InlineData(true, "v1.2.0", "v1.2.5", false, false)]   // patch
    [InlineData(true, "v1.2.0", "v1.2.0", false, false)]   // same release
    [InlineData(true, "v1.2.0", null, false, false)]       // no release
    [InlineData(true, null, "v1.2.0", false, true)]        // read before releases were noted
    [InlineData(false, null, null, false, true)]           // never read
    [InlineData(true, "v1.2.0", "v1.2.1", true, true)]     // forced
    [InlineData(true, "nightly-1", "nightly-2", false, true)]   // not major.minor: any change counts
    public void Recheck_rule(bool hasRead, string? last, string? latest, bool force, bool expected) =>
        Assert.Equal(expected, ReadPolicy.ShouldRead(hasRead, last, latest, force));

    [Fact]
    public async Task Request_carries_preset_budget_and_the_gh_path()
    {
        var calls = new List<HttpRequestMessage>();
        var handler = new StubHandler((request, _) =>
        {
            calls.Add(request);
            return StubHandler.Json("""{"contract_version":"result/v1","usage":{"usd":0.21}}""");
        });
        var options = Options.Create(new FeedEaterOptions { PeerHarvest = new PeerHarvestOptions { ChargehandUrl = "https://chargehand.test", ChargehandKey = "k" } });
        var reader = new ChargehandReader(handler.Client("https://chargehand.test"), options);

        var read = await reader.ReadAsync("gh:acme/loopguard", "the question", CancellationToken.None);

        Assert.Equal(0.21, read.Usd);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://chargehand.test/v1/runs", call.Uri);
        using var body = JsonDocument.Parse(call.Body);
        var root = body.RootElement;
        Assert.Equal("request/v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("the question", root.GetProperty("text").GetString());
        var context = root.GetProperty("context");
        Assert.Equal("prior-art", context.GetProperty("preset").GetString());
        Assert.Equal(0.5, context.GetProperty("budget_usd").GetDouble());
        Assert.False(context.GetProperty("interactive").GetBoolean());
        Assert.Equal("gh:acme/loopguard", context.GetProperty("repository").GetProperty("path").GetString());
        Assert.Equal("Bearer k", calls[0].Headers.Authorization?.ToString());
        Assert.Equal("wait=60", string.Join(",", calls[0].Headers.GetValues("Prefer")));
    }

    [Fact]
    public async Task A_non_200_answer_is_an_error_not_a_read()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""{"error":"no"}""", HttpStatusCode.BadRequest));
        var options = Options.Create(new FeedEaterOptions { PeerHarvest = new PeerHarvestOptions { ChargehandUrl = "https://chargehand.test" } });

        await Assert.ThrowsAsync<HttpRequestException>(() => new ChargehandReader(handler.Client("https://chargehand.test"), options).ReadAsync("gh:a/b", "q", CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"usage":{"usd":0.5}}""", 0.5)]
    [InlineData("""{"usage":{"usd":null}}""", null)]
    [InlineData("""{"usage":{}}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("not json", null)]
    public void Usd_is_null_when_chargehand_could_not_price_the_run(string json, double? expected) => Assert.Equal(expected, ChargehandReader.UsdOf(json));

    [Fact]
    public void Spend_guard_stops_at_the_cap_and_counts_an_unknown_cost_as_the_budget()
    {
        var guard = new SpendGuard(5);
        guard.Add(2.5, 0.5);
        Assert.True(guard.CanStart);
        guard.Add(null, 0.5);
        Assert.Equal(3.0, guard.Spent);
        guard.Add(2.0, 0.5);
        Assert.False(guard.CanStart);
    }

    [Fact]
    public void The_question_names_the_template_the_aspects_and_every_claim_tag()
    {
        var question = PeerQuestion.Build("peer-template-v1", ["architecture", "ops"]);

        Assert.Contains("peer-template-v1", question);
        Assert.Contains("architecture, ops", question);
        foreach (var tag in new[] { "[feature:slug]", "[uses:slug]", "[limitation]", "[addresses]" })
        {
            Assert.Contains(tag, question);
        }
    }

    [Fact]
    public void The_real_peer_set_shape_parses_to_seven_peers_a_template_and_seven_aspects()
    {
        const string yaml = """
            # Peer set for feed-eater: the projects whose code we read.
            repo: Egoushka/feed-eater
            template: peer-template-v1
            aspects: [architecture, features, storage, retrieval, extensibility, ops, limits]
            peers:
              - gh: karakeep-app/karakeep   # read-it-later with AI tagging
              - gh: miniflux/v2
              - gh: FreshRSS/FreshRSS
              - gh: linkwarden/linkwarden
              - gh: wallabag/wallabag
              - gh: readeck/readeck
              - gh: LuanRT/YouTube.js       # not a feed reader, but the same ingest shape
            """;

        var set = PeerSetParser.Parse(yaml);

        Assert.Equal("Egoushka/feed-eater", set.Repo);
        Assert.Equal(7, set.Peers.Count);
        Assert.Contains("karakeep-app/karakeep", set.Peers);
        Assert.Contains("LuanRT/YouTube.js", set.Peers);
        Assert.Equal("peer-template-v1", set.Template);
        Assert.Equal(7, set.Aspects.Count);
        Assert.Equal("limits", set.Aspects[^1]);
        Assert.Equal(8, set.All.Count);
    }

    [Fact]
    public void The_older_plain_peer_list_still_parses()
    {
        var set = PeerSetParser.Parse("repo: a/b\npeers:\n  - c/d\n  - e/f\n");

        Assert.Equal(["c/d", "e/f"], set.Peers);
        Assert.Equal("peer-template-v1", set.Template);
        Assert.Empty(set.Aspects);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("12-30", 30)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    public void Last_cited_line(string? lines, int? expected) => Assert.Equal(expected, PriorArtVerify.LastLine(lines));

    // --- check 3: gap computation over a fixture ---

    private static readonly Guid Fts = Guid.NewGuid(), FtsAlias = Guid.NewGuid(), Vector = Guid.NewGuid(), Retry = Guid.NewGuid(), OwnThing = Guid.NewGuid(), Boast = Guid.NewGuid();
    private static readonly Guid ToolA = Guid.NewGuid(), ToolB = Guid.NewGuid(), ToolC = Guid.NewGuid(), Own = Guid.NewGuid();

    private static readonly List<FeatureEntity> Entities =
    [
        new() { Id = Fts, Name = "full text search", ExternalRef = "feature:full-text-search" },
        new() { Id = FtsAlias, Name = "full text search engine", ExternalRef = "feature:full-text-search-engine", MergedInto = Fts },
        new() { Id = Vector, Name = "vector search", ExternalRef = "feature:vector-search" },
        new() { Id = Retry, Name = "retry with backoff", ExternalRef = "feature:retry-with-backoff" },
        new() { Id = OwnThing, Name = "digest brief", ExternalRef = "feature:digest-brief" },
        new() { Id = Boast, Name = "blazing fast", ExternalRef = "feature:blazing-fast" },
    ];

    private static FeatureFactRow Fact(Guid tool, string name, string toolRef, Guid feature, double confidence = 0.9, string? label = null) => new()
    {
        FactId = Guid.NewGuid(),
        ToolId = tool,
        ToolName = name,
        ToolRef = toolRef,
        FeatureId = feature,
        Text = $"{name} has it.",
        Locator = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["repo"] = toolRef[3..],
            ["commit"] = "abc123",
            ["path"] = "src/x.cs",
            ["lines"] = "1-9",
            ["confidence"] = confidence,
            ["label"] = label,
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value)),
    };

    private static ApproachUse Use(Guid tool, string name, string toolRef, string approach) => new() { ApproachId = Guid.NewGuid(), ApproachName = approach, ToolId = tool, ToolName = name, ToolRef = toolRef };

    private static readonly DateTimeOffset At = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    private static GapReport Compute(int minPeers = 2, IReadOnlySet<string>? peers = null, IReadOnlyList<FeatureFactRow>? extra = null)
    {
        var facts = new List<FeatureFactRow>
        {
            Fact(ToolA, "a", "gh:p/a", Fts),
            Fact(ToolB, "b", "gh:p/b", FtsAlias),            // the same feature under its merged name: counted once with A's
            Fact(ToolB, "b", "gh:p/b", Fts, 0.5),            // B twice for one feature: still one peer
            Fact(ToolA, "a", "gh:p/a", Retry),
            Fact(ToolB, "b", "gh:p/b", Retry),
            Fact(ToolC, "c", "gh:p/c", Vector),
            Fact(ToolA, "a", "gh:p/a", Boast, label: "marketing"),
            Fact(ToolB, "b", "gh:p/b", Boast, label: "marketing"),
            Fact(Own, "own", "gh:me/own", Retry),
            Fact(Own, "own", "gh:me/own", OwnThing),
        };
        facts.AddRange(extra ?? []);
        var uses = new List<ApproachUse>
        {
            Use(ToolA, "a", "gh:p/a", "bm25"), Use(ToolB, "b", "gh:p/b", "bm25"), Use(ToolC, "c", "gh:p/c", "hnsw"), Use(Own, "own", "gh:me/own", "bm25"),
        };
        return GapCompute.Compute("me/own", At, minPeers, "test", peers, facts, Entities, uses);
    }

    [Fact]
    public void A_feature_two_peers_have_and_the_own_repo_lacks_is_a_gap_with_both_peers_and_evidence()
    {
        var report = Compute();

        var gap = Assert.Single(report.Gaps);
        Assert.Equal("full text search", gap.Feature);
        Assert.Equal("full-text-search", gap.Slug);
        Assert.Equal(["a", "b"], gap.Peers.Select(p => p.Tool).ToArray());
        Assert.All(gap.Peers, p => Assert.Equal("src/x.cs", p.Evidence.Path));
        Assert.Equal(0.9, gap.Peers.Single(p => p.Tool == "b").Confidence);   // B's best fact for the merged feature
        Assert.Equal("bm25", Assert.Single(gap.Approaches).Approach);
    }

    [Fact]
    public void Min_peers_one_adds_the_features_a_single_peer_has_but_not_the_ones_the_own_repo_has()
    {
        var report = Compute(minPeers: 1);

        Assert.Equal(["full text search", "vector search"], report.Gaps.Select(g => g.Feature).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(report.Gaps, g => g.Feature == "retry with backoff");
    }

    [Fact]
    public void Marketing_labelled_facts_count_for_nothing()
    {
        var report = Compute(minPeers: 1);

        Assert.DoesNotContain(report.Gaps, g => g.Feature == "blazing fast");
    }

    [Fact]
    public void A_feature_only_the_own_repo_has_is_listed_as_own_only()
    {
        var report = Compute();

        Assert.Equal("digest brief", Assert.Single(report.OwnOnly).Feature);
    }

    [Fact]
    public void Shared_approaches_need_min_peers_tools_and_the_own_repo_is_not_one_of_them()
    {
        var report = Compute();

        var shared = Assert.Single(report.SharedApproaches);
        Assert.Equal("bm25", shared.Approach);
        Assert.Equal(["a", "b"], shared.Tools.ToArray());
    }

    [Fact]
    public void A_peer_scope_leaves_out_the_other_tools()
    {
        var report = Compute(peers: new HashSet<string>(["gh:p/a"], StringComparer.OrdinalIgnoreCase));

        Assert.Empty(report.Gaps);   // B no longer counts, so no feature has two peers
        Assert.Equal(["a"], report.Peers.ToArray());
    }

    [Fact]
    public void By_peer_lists_the_gaps_each_peer_could_fill()
    {
        var report = Compute();

        Assert.Equal(["full text search"], report.ByPeer.Single(p => p.Peer == "a").Missing.ToArray());
        Assert.Equal(["full text search"], report.ByPeer.Single(p => p.Peer == "b").Missing.ToArray());
        Assert.Empty(report.ByPeer.Single(p => p.Peer == "c").Missing);
    }
}
