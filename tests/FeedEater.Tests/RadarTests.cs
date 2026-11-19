using System.Net;
using System.Text;
using System.Text.Json;
using FeedEater.Cards;
using FeedEater.Radar;
using Microsoft.Extensions.Options;
using Xunit;

namespace FeedEater.Tests;

public sealed class RadarTests
{
    private static readonly IReadOnlyList<ManifestRepo> Manifest =
    [
        new("mine", "personal/mine", "personal", "active"),
        new("lab", "personal/lab", "infra", "active"),
        new("job", "work/job", "work", "active"),
    ];

    private static CardRow Card(long id, string repo, string state, string title = "Some tool", string reason = "it saves time", string term = "sometool") => new(
        id, term, title, "what", repo, "where", [], false, "small", "gain", state, reason,
        new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), []);

    private static DenyList Deny(out string dir)
    {
        dir = Directory.CreateTempSubdirectory("radar-deny").FullName;
        return DenyList.Load(CardsParserTests.DenyFile(dir))!;
    }

    private static string Build(IReadOnlyList<CardRow> cards, DenyList? deny, IReadOnlyDictionary<long, string>? results = null) =>
        RadarRules.Build(cards, Manifest, [], results ?? new Dictionary<long, string>(), deny);

    [Fact]
    public void Radar_never_exports_work_repos()
    {
        var deny = Deny(out var dir);
        try
        {
            var json = Build(
            [
                Card(1, "mine", "adopt", "Mine tool"),
                Card(2, "job", "adopt", "Work tool"),
                Card(3, "job", "trial", "Another work tool"),
                Card(4, "lab", "hold", "Infra tool"),
                Card(5, "ghost", "hold", "Unknown repo tool"),
                Card(6, "mine", "proposed", "Not yet picked"),
                Card(7, "mine", "dropped", "Dropped one"),
            ], deny);

            var names = JsonDocument.Parse(json).RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
            Assert.Equal(["Mine tool"], names);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Radar_export_validates_against_the_site_schema()
    {
        var deny = Deny(out var dir);
        try
        {
            var json = Build(
            [
                Card(1, "mine", "adopt", new string('n', 120), new string('r', 400)),
                Card(2, "mine", "trial", "Second"),
            ], deny, new Dictionary<long, string> { [2] = "10 tools: 9122 to 7355 bytes\n\nbuild clean" });

            Assert.Empty(RadarSchema.Check(json));
            var entries = JsonDocument.Parse(json).RootElement.GetProperty("entries");
            Assert.Equal(2, entries.GetArrayLength());
            Assert.True(entries[0].GetProperty("name").GetString()!.Length <= RadarRules.MaxName);
            Assert.Equal("2026-11-19", entries[0].GetProperty("since").GetString());
            Assert.Equal("10 tools: 9122 to 7355 bytes build clean", entries[1].GetProperty("result").GetString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("""{"entries":[{"name":"x","quadrant":"infra","ring":"maybe","since":"2026-11-19","reason":"r","repoKind":"personal"}]}""")]
    [InlineData("""{"entries":[{"name":"x","quadrant":"infra","ring":"hold","since":"2026-11-19","reason":"r","repoKind":"work"}]}""")]
    [InlineData("""{"entries":[{"name":"x","quadrant":"infra","ring":"hold","since":"19 Nov","reason":"r","repoKind":"personal"}]}""")]
    [InlineData("""{"entries":[{"name":"x","quadrant":"infra","ring":"hold","since":"2026-11-19","repoKind":"personal"}]}""")]
    [InlineData("""{"entries":[{"name":"x","quadrant":"infra","ring":"hold","since":"2026-11-19","reason":"r","repoKind":"personal","extra":1}]}""")]
    public void Radar_schema_rejects_a_row_that_breaks_it(string json) => Assert.NotEmpty(RadarSchema.Check(json));

    [Fact]
    public void Radar_export_rejects_denylisted_terms()
    {
        var deny = Deny(out var dir);
        try
        {
            var fake = CardsParserTests.Fake;
            Assert.Throws<RadarExportException>(() => Build([Card(1, "mine", "adopt", $"Tool for {fake.ToUpperInvariant()}")], deny));
            Assert.Throws<RadarExportException>(() => Build([Card(1, "mine", "adopt", reason: $"used at {fake}")], deny));
            Assert.Throws<RadarExportException>(() => Build([Card(1, "mine", "trial")], deny, new Dictionary<long, string> { [1] = $"ran against {fake}" }));
            // One bad row fails the whole export; a denied card of a work repo is never a row, so it cannot fail it.
            Assert.Throws<RadarExportException>(() => Build([Card(1, "mine", "adopt"), Card(2, "mine", "hold", $"{fake} thing")], deny));
            Assert.NotEmpty(Build([Card(1, "mine", "adopt"), Card(2, "job", "hold", $"{fake} thing")], deny));
            // No readable list: fail closed.
            Assert.Throws<RadarExportException>(() => Build([Card(1, "mine", "adopt")], null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string? BranchFile { get; set; }
        public string? BaseFile { get; set; }
        public bool OpenPr { get; set; }
        public bool BranchExists { get; set; }

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string File(string text) =>
            JsonSerializer.Serialize(new { sha = "blob1", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add($"{request.Method} {path}");
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            HttpResponseMessage reply = (request.Method.Method, path) switch
            {
                ("GET", "/repos/o/site") => Json("""{"default_branch":"main"}"""),
                ("GET", var p) when p.StartsWith("/repos/o/site/contents/content/radar.json?ref=main", StringComparison.Ordinal) =>
                    BaseFile is null ? Json("{}", HttpStatusCode.NotFound) : Json(File(BaseFile)),
                ("GET", var p) when p.StartsWith("/repos/o/site/contents/content/radar.json?ref=radar", StringComparison.Ordinal) =>
                    BranchFile is null ? Json("{}", HttpStatusCode.NotFound) : Json(File(BranchFile)),
                ("GET", "/repos/o/site/git/ref/heads/main") => Json("""{"object":{"sha":"abc"}}"""),
                ("GET", "/repos/o/site/git/ref/heads/radar/export") => BranchExists ? Json("""{"object":{"sha":"abc"}}""") : Json("{}", HttpStatusCode.NotFound),
                ("POST", "/repos/o/site/git/refs") => Json("{}", HttpStatusCode.Created),
                ("PUT", "/repos/o/site/contents/content/radar.json") => Json("{}"),
                ("GET", var p) when p.StartsWith("/repos/o/site/pulls?", StringComparison.Ordinal) => Json(OpenPr ? """[{"html_url":"https://github.com/o/site/pull/1"}]""" : "[]"),
                ("POST", "/repos/o/site/pulls") => Json("""{"html_url":"https://github.com/o/site/pull/2"}""", HttpStatusCode.Created),
                _ => throw new InvalidOperationException($"unexpected {request.Method} {path}"),
            };
            return Task.FromResult(reply);
        }
    }

    private static RadarPublisher Publisher(FakeGitHub handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
        Options.Create(new FeedEaterOptions { GitHub = new GitHubOptions { Token = "t" }, Radar = new RadarOptions { Enabled = true, Repo = "o/site" } }));

    [Fact]
    public async Task Radar_publish_opens_a_pull_request_from_a_new_branch()
    {
        var handler = new FakeGitHub { BaseFile = "old" };
        var result = await Publisher(handler).PublishAsync("new\n", default);

        Assert.Equal(new RadarPublished("opened", "https://github.com/o/site/pull/2"), result);
        Assert.Contains("POST /repos/o/site/git/refs", handler.Calls);
        Assert.Contains("PUT /repos/o/site/contents/content/radar.json", handler.Calls);
        Assert.DoesNotContain(handler.Calls, c => c.StartsWith("PATCH", StringComparison.Ordinal) || c.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Radar_publish_reuses_the_open_pull_request_and_skips_an_unchanged_file()
    {
        var reuse = new FakeGitHub { BaseFile = "old", BranchExists = true, BranchFile = "old", OpenPr = true };
        Assert.Equal(new RadarPublished("updated", "https://github.com/o/site/pull/1"), await Publisher(reuse).PublishAsync("new\n", default));
        Assert.DoesNotContain("POST /repos/o/site/pulls", reuse.Calls);
        Assert.DoesNotContain("POST /repos/o/site/git/refs", reuse.Calls);

        var same = new FakeGitHub { BaseFile = "same\n" };
        Assert.Equal(new RadarPublished("unchanged", null), await Publisher(same).PublishAsync("same\n", default));
        Assert.DoesNotContain(same.Calls, c => c.StartsWith("PUT", StringComparison.Ordinal) || c.StartsWith("POST", StringComparison.Ordinal));
    }
}
