using System.Net;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Hype;
using FeedEater.Loops;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutopsyJobTests(PostgresFixture pg) : IAsyncLifetime
{
    // 2026-11-01 09:10 in Kyiv (UTC+2 since 2026-10-25): the monthly run is due.
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 7, 10, 0, TimeSpan.Zero);

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Rig(AutopsyJob Job, FakeTimeProvider Time, StubHandler Github, List<string> Sent);

    /// <summary><paramref name="repos"/>: name to (stars, days since the last push, archived). A repo that is not listed answers 404.</summary>
    private static Func<string, HttpResponseMessage> Github(Dictionary<string, (int Stars, int PushedDaysAgo, bool Archived)> repos) => path =>
    {
        var name = string.Join('/', path.Trim('/').Split('/').Skip(1).Take(2));
        if (!repos.TryGetValue(name, out var r))
        {
            return StubHandler.Json("{}", HttpStatusCode.NotFound);
        }

        return path.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? StubHandler.Json("""{"tag_name":"v2","published_at":"2026-10-20T10:00:00Z"}""")
            : StubHandler.Json($$"""{"full_name":"{{name}}","created_at":"2024-01-01T00:00:00Z","pushed_at":"{{Now.AddDays(-r.PushedDaysAgo):O}}","stargazers_count":{{r.Stars}},"archived":{{(r.Archived ? "true" : "false")}}}""");
    };

    private Rig Build(Func<string, HttpResponseMessage> github, bool telegramFails = false, string token = "")
    {
        var time = new FakeTimeProvider(Now);
        var sent = new List<string>();
        var options = Options.Create(new FeedEaterOptions
        {
            TimeZone = "Europe/Kyiv", Telegram = new TelegramOptions { AllowedUserId = 42 }, GitHub = new GitHubOptions { User = "octocat", Token = token },
        });
        var stub = new StubHandler((_, body) =>
        {
            if (telegramFails)
            {
                return StubHandler.Json("""{"ok":false,"description":"Too Many Requests"}""");
            }

            sent.Add(body);
            return StubHandler.Json("""{"ok":true,"result":{"message_id":1}}""");
        });
        var ghStub = new StubHandler((request, _) => github(request.RequestUri!.AbsolutePath));
        var job = new AutopsyJob(
            new AutopsyStore(pg.Db), new GitHubStarsClient(ghStub.Client("http://github/"), options), new TelegramClient(stub.Client("http://tg/botT/")),
            new QuietHours(new CursorStore(pg.Db), options, time), new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<AutopsyJob>.Instance);
        return new Rig(job, time, ghStub, sent);
    }

    private async Task<long> SnapshotAsync(string title, string? repo, int? stars = 100, int ageDays = 90, long feed = 1, int? relevance = 3)
    {
        var id = await Seed.ItemAsync(pg, feed, title, TestVectors.OneHot(1), Now.UtcDateTime.AddDays(-ageDays - 1));
        await new AutopsyStore(pg.Db).AddAsync(new RepoSnapshot(id, repo, Now.AddDays(-ageDays), stars, Now.AddDays(-ageDays - 3), "v1", Now.AddYears(-1)), default);
        if (relevance is { } r)
        {
            await using var c = await pg.Db.DataSource.OpenConnectionAsync();
            await c.ExecuteAsync("insert into triage (item_id, relevance, kind, model) values (@id, @r, 'new', 'm')", new { id, r });
        }

        return id;
    }

    private async Task<string?> MonthOfAsync(long id)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<string?>("select autopsy_month::text from repo_snapshots where item_id = @id", new { id });
    }

    private async Task<List<string?>> MonthsOfAsync(IEnumerable<long> ids)
    {
        var months = new List<string?>();
        foreach (var id in ids)
        {
            months.Add(await MonthOfAsync(id));
        }

        return months;
    }

    private static readonly Dictionary<string, (int, int, bool)> Mixed = new()
    {
        ["acme/grew"] = (150, 3, false), ["acme/alive"] = (100, 5, false), ["acme/quiet"] = (100, 100, false), ["acme/archived"] = (500, 1, true),
    };

    private async Task<long[]> SeedMixedAsync() =>
    [
        await SnapshotAsync("Grew", "acme/grew", feed: 1, relevance: 3),
        await SnapshotAsync("Alive", "acme/alive", feed: 1, relevance: 3),
        await SnapshotAsync("Quiet", "acme/quiet", feed: 2, relevance: 2),
        await SnapshotAsync("Archived", "acme/archived", feed: 2, relevance: 2),
        await SnapshotAsync("Deleted", "acme/deleted", feed: 2, relevance: 2),
        await SnapshotAsync("No repo", null, stars: null),
        await SnapshotAsync("Already gone", "acme/early", stars: null),
    ];

    [Fact]
    public async Task The_monthly_run_scores_the_ripe_snapshots_stores_the_autopsy_and_sends_it()
    {
        var ids = await SeedMixedAsync();
        var rig = Build(Github(Mixed.ToDictionary(kv => kv.Key, kv => kv.Value)));

        await rig.Job.TickAsync(default);

        var row = (await new AutopsyStore(pg.Db).LatestAsync(default))!;
        var r = row.Report;
        Assert.Equal("2026-11-01", row.Month);
        Assert.NotNull(row.SentAt);
        Assert.Equal(new VerdictCounts(1, 1, 1, 2), r.Total);   // grew, alive, quiet; archived and deleted are gone
        Assert.Equal((1, 1, 0), (r.NoRepo, r.NoBaseline, r.Unchecked));
        Assert.Equal(["3", "2"], r.ByRelevance.Select(g => g.Label));
        Assert.Equal(new VerdictCounts(1, 1, 0, 0), r.ByRelevance[0].Counts);
        Assert.Equal(new VerdictCounts(0, 0, 1, 2), r.ByRelevance[1].Counts);
        Assert.Equal(["Feed 1", "Feed 2"], r.ByFeed.Select(g => g.Label));
        var grew = r.Items.Single(i => i.ItemId == ids[0]);
        Assert.Equal((RepoVerdict.Grew, 100, 150, 50.0, true, true), (grew.Verdict, grew.StarsThen, grew.StarsNow, grew.StarsGrowthPercent, grew.PushedRecently, grew.NewRelease));
        Assert.Equal(RepoVerdict.Gone, r.Items.Single(i => i.ItemId == ids[4]).Verdict);
        Assert.Null(r.Items.Single(i => i.ItemId == ids[4]).StarsNow);
        Assert.All(await MonthsOfAsync(ids), m => Assert.Equal("2026-11-01", m));

        var message = Assert.Single(rig.Sent);
        Assert.Contains("Hype autopsy", message, StringComparison.Ordinal);
        Assert.Contains("stars up 20% or more", message, StringComparison.Ordinal);
        Assert.Contains("\"chat_id\":42", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_runs_once_per_month_and_a_month_with_nothing_ripe_sends_nothing()
    {
        await SeedMixedAsync();
        var rig = Build(Github(Mixed.ToDictionary(kv => kv.Key, kv => kv.Value)));

        await rig.Job.TickAsync(default);
        await rig.Job.TickAsync(default);
        var githubCalls = rig.Github.Calls.Count;
        rig.Time.Advance(TimeSpan.FromDays(30));   // 2026-12-01 09:10: due again, but every snapshot was scored
        await rig.Job.TickAsync(default);

        Assert.Single(rig.Sent);
        Assert.Equal(githubCalls, rig.Github.Calls.Count);
        Assert.Equal(["2026-11-01"], await new AutopsyStore(pg.Db).ListAsync(10, default));
    }

    [Fact]
    public async Task A_snapshot_that_ripens_later_is_scored_in_a_later_month_not_lost()
    {
        var rig = Build(Github(new() { ["acme/grew"] = (150, 3, false) }));
        await SnapshotAsync("Early", "acme/grew", ageDays: 90);
        await SnapshotAsync("Late", "acme/grew", ageDays: 60);   // 87 days old only on 2026-11-28

        await rig.Job.TickAsync(default);
        rig.Time.Advance(TimeSpan.FromDays(30));
        await rig.Job.TickAsync(default);

        var store = new AutopsyStore(pg.Db);
        Assert.Equal(2, rig.Sent.Count);
        Assert.Equal(["Early"], (await store.GetAsync("2026-11-01", default))!.Report.Items.Select(i => i.Title));
        Assert.Equal(["Late"], (await store.GetAsync("2026-12-01", default))!.Report.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Nothing_ripe_means_no_request_no_row_and_no_message()
    {
        await SnapshotAsync("Young", "acme/grew", ageDays: 40);
        var rig = Build(Github(new() { ["acme/grew"] = (150, 3, false) }));

        await rig.Job.TickAsync(default);

        Assert.Empty(rig.Sent);
        Assert.Empty(rig.Github.Calls);
        Assert.Null(await new AutopsyStore(pg.Db).LatestAsync(default));
    }

    [Fact]
    public async Task Quiet_hours_hold_the_autopsy_until_they_end()
    {
        await SeedMixedAsync();
        var rig = Build(Github(Mixed.ToDictionary(kv => kv.Key, kv => kv.Value)));
        await new CursorStore(pg.Db).SetAsync("quiet:manual", $"on|{Now.AddHours(2):O}", default);

        await rig.Job.TickAsync(default);
        Assert.Empty(rig.Sent);
        Assert.Empty(rig.Github.Calls);

        rig.Time.Advance(TimeSpan.FromHours(3));
        await rig.Job.TickAsync(default);
        Assert.Single(rig.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refused_call_leaves_that_repo_and_the_rest_for_next_month(HttpStatusCode refusal)
    {
        var first = await SnapshotAsync("First", "acme/grew");
        var second = await SnapshotAsync("Second", "acme/alive");
        var third = await SnapshotAsync("Third", "acme/quiet");
        var refuse = true;
        var rig = Build(path => refuse && path.Contains("/acme/alive", StringComparison.Ordinal) ? StubHandler.Json("{}", refusal) : Github(Mixed.ToDictionary(kv => kv.Key, kv => kv.Value))(path));

        await rig.Job.TickAsync(default);

        var r = (await new AutopsyStore(pg.Db).LatestAsync(default))!.Report;
        Assert.Equal([first], r.Items.Select(i => i.ItemId));
        Assert.Equal(2, r.Unchecked);
        Assert.Equal(3, rig.Github.Calls.Count);   // first (2 requests), second refused; third not asked
        Assert.Equal(new string?[] { "2026-11-01", null, null }, await MonthsOfAsync([first, second, third]));

        refuse = false;
        rig.Time.Advance(TimeSpan.FromDays(30));
        await rig.Job.TickAsync(default);

        var next = (await new AutopsyStore(pg.Db).GetAsync("2026-12-01", default))!.Report;
        Assert.Equal([second, third], next.Items.Select(i => i.ItemId).Order());
    }

    [Fact]
    public async Task When_GitHub_answers_nothing_there_is_no_report_and_every_snapshot_waits()
    {
        var ids = new[] { await SnapshotAsync("One", "acme/grew"), await SnapshotAsync("Two", "acme/alive"), await SnapshotAsync("Zero", null, stars: null) };
        var rig = Build(_ => StubHandler.Json("{}", HttpStatusCode.TooManyRequests));

        await rig.Job.TickAsync(default);

        Assert.Empty(rig.Sent);
        Assert.Null(await new AutopsyStore(pg.Db).LatestAsync(default));
        Assert.All(await MonthsOfAsync(ids), Assert.Null);
        Assert.Single(rig.Github.Calls);   // the first refusal stops the run
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection")]
    [InlineData("garbage")]
    public async Task A_timeout_a_dead_connection_or_an_unreadable_answer_leaves_the_repo_for_next_month(string failure)
    {
        var id = await SnapshotAsync("One", "acme/grew");
        var rig = Build(failure == "garbage"
            ? _ => StubHandler.Json("""{"full_name":"x"}""")
            : _ => throw (failure == "timeout" ? new TaskCanceledException("timed out", new TimeoutException()) : new HttpRequestException("Connection refused")));

        await rig.Job.TickAsync(default);

        Assert.Empty(rig.Sent);
        Assert.Null(await MonthOfAsync(id));
    }

    [Fact]
    public async Task Without_a_token_the_run_stops_at_forty_requests_and_the_rest_wait_for_next_month()
    {
        var repos = new Dictionary<string, (int, int, bool)>();
        for (var i = 0; i < 25; i++)
        {
            repos[$"acme/r{i:00}"] = (100, 5, false);
            await SnapshotAsync($"Item {i}", $"acme/r{i:00}");
        }

        var rig = Build(Github(repos));

        await rig.Job.TickAsync(default);

        var r = (await new AutopsyStore(pg.Db).LatestAsync(default))!.Report;
        Assert.Equal((20, 5), (r.Items.Count, r.Unchecked));
        Assert.Equal(40, rig.Github.Calls.Count);
    }

    [Fact]
    public async Task With_a_token_every_snapshot_is_checked_in_one_run()
    {
        var repos = new Dictionary<string, (int, int, bool)>();
        for (var i = 0; i < 25; i++)
        {
            repos[$"acme/r{i:00}"] = (100, 5, false);
            await SnapshotAsync($"Item {i}", $"acme/r{i:00}");
        }

        var rig = Build(Github(repos), token: "gh-token");

        await rig.Job.TickAsync(default);

        var r = (await new AutopsyStore(pg.Db).LatestAsync(default))!.Report;
        Assert.Equal((25, 0), (r.Items.Count, r.Unchecked));
    }

    [Fact]
    public async Task A_failed_send_keeps_the_autopsy_unsent_and_the_retry_rebuilds_the_same_month()
    {
        await SeedMixedAsync();
        var rig = Build(Github(Mixed.ToDictionary(kv => kv.Key, kv => kv.Value)), telegramFails: true);
        var store = new AutopsyStore(pg.Db);

        await Assert.ThrowsAsync<TelegramException>(() => rig.Job.TickAsync(default));

        var stored = (await store.LatestAsync(default))!;
        Assert.Null(stored.SentAt);   // stored, so /ui/autopsy has it, but not marked sent
        Assert.Equal(5, stored.Report.Items.Count);

        // The retry for the same month still sees the rows it scored.
        var retry = Build(Github(Mixed.ToDictionary(kv => kv.Key, kv => kv.Value)));
        await retry.Job.TickAsync(default);

        var after = (await store.LatestAsync(default))!;
        Assert.NotNull(after.SentAt);
        Assert.Equal(5, after.Report.Items.Count);
        Assert.Single(await store.ListAsync(10, default));
        Assert.Single(retry.Sent);
    }
}
