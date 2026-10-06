using System.Net;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Hype;
using FeedEater.Loops;
using FeedEater.Signals;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RepoSnapshotJobTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 3, 30, 0, TimeSpan.Zero);   // 05:30 in Kyiv

    private const string Repo = """{"full_name":"acme/widget","created_at":"2024-03-02T08:00:00Z","pushed_at":"2026-10-20T09:00:00Z","stargazers_count":100}""";
    private const string Release = """{"tag_name":"v1.0.0","published_at":"2026-09-20T10:00:00Z"}""";

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpResponseMessage Ok(string path) => StubHandler.Json(path.EndsWith("/releases/latest", StringComparison.Ordinal) ? Release : Repo);

    private (RepoSnapshotJob Job, StubHandler Github, FakeTimeProvider Time) Build(Func<string, HttpResponseMessage>? answer = null, string token = "", DateTimeOffset? at = null)
    {
        var time = new FakeTimeProvider(at ?? Now);
        var options = Options.Create(new FeedEaterOptions { TimeZone = "Europe/Kyiv", GitHub = new GitHubOptions { User = "octocat", Token = token } });
        var github = new StubHandler((request, _) => (answer ?? Ok)(request.RequestUri!.AbsolutePath));
        var job = new RepoSnapshotJob(
            new GitHubStarsClient(github.Client("http://github/"), options), new AutopsyStore(pg.Db), new CursorStore(pg.Db), options,
            new LoopHealth(time), time, NullLogger<RepoSnapshotJob>.Instance);
        return (job, github, time);
    }

    private async Task<long> LikeAsync(string title, string? repo, short value = 1)
    {
        var id = await Seed.ItemAsync(pg, 1, title, TestVectors.OneHot(1), content: repo is null ? "no link here" : $"worth a look: https://github.com/{repo}");
        await new FeedbackStore(pg.Db).SetVoteAsync(id, value, default);
        return id;
    }

    private async Task<List<Row>> RowsAsync()
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.QueryAsync<Row>("select item_id, repo, stars, release_tag, taken_at from repo_snapshots order by item_id")).ToList();
    }

    private sealed record Row(long ItemId, string? Repo, int? Stars, string? ReleaseTag, DateTime TakenAt);

    [Fact]
    public async Task Only_liked_items_are_snapshotted_with_the_facts_of_their_repo()
    {
        var liked = await LikeAsync("Liked", "acme/widget");
        await LikeAsync("Disliked", "acme/other", value: -1);
        await Seed.ItemAsync(pg, 1, "Unvoted", TestVectors.OneHot(2), content: "https://github.com/acme/unvoted");
        var (job, github, _) = Build();

        Assert.Equal(1, await job.SnapshotAsync(default));

        var row = Assert.Single(await RowsAsync());
        Assert.Equal((liked, "acme/widget", 100, "v1.0.0"), (row.ItemId, row.Repo, row.Stars, row.ReleaseTag));
        Assert.Equal(Now.UtcDateTime, row.TakenAt);
        Assert.Equal(2, github.Calls.Count);
        Assert.All(github.Calls, c => Assert.Contains("repos/acme/widget", c.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_repo_is_found_in_the_item_url_too_and_an_item_without_one_gets_a_row_with_no_repo_and_no_request()
    {
        var byUrl = await LikeAsync("By url", null);
        var without = await LikeAsync("No repo", null);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update items set url = 'https://github.com/acme/widget/issues/4' where id = @byUrl", new { byUrl });
        }

        var (job, github, _) = Build();

        Assert.Equal(2, await job.SnapshotAsync(default));

        var rows = await RowsAsync();
        Assert.Equal("acme/widget", rows.Single(r => r.ItemId == byUrl).Repo);
        Assert.Null(rows.Single(r => r.ItemId == without).Repo);
        Assert.Equal(2, github.Calls.Count);   // only the first one asked GitHub
    }

    [Fact]
    public async Task An_item_that_has_a_snapshot_is_not_looked_up_again()
    {
        await LikeAsync("Liked", "acme/widget");
        var (job, github, _) = Build();

        await job.SnapshotAsync(default);
        var calls = github.Calls.Count;
        Assert.Equal(0, await job.SnapshotAsync(default));

        Assert.Equal(calls, github.Calls.Count);
    }

    [Fact]
    public async Task The_job_runs_once_per_day_key_and_takes_the_next_days_likes_the_next_day()
    {
        await LikeAsync("First", "acme/one");
        var (job, github, time) = Build();

        await job.TickAsync(default);
        await job.TickAsync(default);
        var firstDay = github.Calls.Count;
        await LikeAsync("Second", "acme/two");
        await job.TickAsync(default);   // same key: nothing new until tomorrow
        Assert.Equal(firstDay, github.Calls.Count);

        time.Advance(TimeSpan.FromDays(1));
        await job.TickAsync(default);

        Assert.Equal(2, firstDay);
        Assert.Equal(4, github.Calls.Count);
        Assert.Equal(["acme/one", "acme/two"], (await RowsAsync()).Select(r => r.Repo));
    }

    [Fact]
    public async Task Before_five_in_the_morning_the_run_belongs_to_the_day_before()
    {
        await LikeAsync("First", "acme/one");
        var (job, github, time) = Build(at: new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero));   // 04:00 Kyiv

        await job.TickAsync(default);   // a missed run starts at once, under yesterday's key
        time.Advance(TimeSpan.FromHours(2));
        await job.TickAsync(default);   // 06:00: today's key is due

        await LikeAsync("Second", "acme/two");
        time.Advance(TimeSpan.FromHours(1));
        await job.TickAsync(default);

        Assert.Equal(2, github.Calls.Count);   // the day keys were 10-31 and 11-01; the third tick found nothing due
    }

    [Fact]
    public async Task Without_a_token_a_run_stops_at_forty_requests_and_the_next_run_carries_on()
    {
        for (var i = 0; i < 25; i++)
        {
            await LikeAsync($"Liked {i}", $"acme/r{i:00}");
        }

        var (job, github, _) = Build();

        Assert.Equal(20, await job.SnapshotAsync(default));
        Assert.Equal(40, github.Calls.Count);
        Assert.Equal(5, await job.SnapshotAsync(default));
        Assert.Equal(50, github.Calls.Count);
        Assert.Equal(25, (await RowsAsync()).Count);
    }

    [Fact]
    public async Task A_404_costs_one_request_so_more_repos_fit_in_the_budget()
    {
        for (var i = 0; i < 45; i++)
        {
            await LikeAsync($"Liked {i}", $"acme/r{i:00}");
        }

        var (job, github, _) = Build(_ => StubHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Equal(39, await job.SnapshotAsync(default));   // the check keeps room for a repo's second request, so it never goes past 40
        Assert.Equal(39, github.Calls.Count);
    }

    [Fact]
    public async Task With_a_token_there_is_no_per_run_cap()
    {
        for (var i = 0; i < 25; i++)
        {
            await LikeAsync($"Liked {i}", $"acme/r{i:00}");
        }

        var (job, github, _) = Build(token: "gh-token");

        Assert.Equal(25, await job.SnapshotAsync(default));
        Assert.Equal(50, github.Calls.Count);
    }

    [Fact]
    public async Task A_repo_that_is_already_gone_is_stored_with_no_stars_and_not_asked_again()
    {
        await LikeAsync("Liked", "acme/gone");
        var (job, github, _) = Build(_ => StubHandler.Json("{}", HttpStatusCode.NotFound));

        Assert.Equal(1, await job.SnapshotAsync(default));
        Assert.Equal(0, await job.SnapshotAsync(default));

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(("acme/gone", null), (row.Repo, row.Stars));
        Assert.Single(github.Calls);
    }

    /// <summary>What GitHub answers when the hourly limit is used up: 429, or 403 with no requests remaining.</summary>
    private static HttpResponseMessage RateLimited(HttpStatusCode status)
    {
        var response = StubHandler.Json("{}", status);
        if (status == HttpStatusCode.Forbidden)
        {
            response.Headers.Add("X-RateLimit-Remaining", "0");
        }

        return response;
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_rate_limit_ends_the_run_cleanly_and_the_item_is_taken_the_next_day(HttpStatusCode refusal)
    {
        await LikeAsync("First", "acme/one");
        await LikeAsync("Second", "acme/two");
        await LikeAsync("Third", "acme/three");
        var limited = true;
        var (job, github, time) = Build(path => limited && path.Contains("/acme/two", StringComparison.Ordinal) ? RateLimited(refusal) : Ok(path));

        await job.TickAsync(default);   // does not throw: the run just ends, and its key is kept

        Assert.Equal(["acme/one"], (await RowsAsync()).Select(r => r.Repo));
        Assert.Equal(3, github.Calls.Count);   // one, then two (refused); three never asked
        limited = false;
        await job.TickAsync(default);
        Assert.Equal(3, github.Calls.Count);   // same day: no retry

        time.Advance(TimeSpan.FromDays(1));
        await job.TickAsync(default);

        Assert.Equal(["acme/one", "acme/two", "acme/three"], (await RowsAsync()).Select(r => r.Repo));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]   // a 403 that is not the rate limit
    public async Task A_repo_that_keeps_failing_does_not_hold_back_the_later_likes_and_is_given_up_after_three_runs(HttpStatusCode failure)
    {
        await LikeAsync("Blocked", "acme/blocked");
        await LikeAsync("Second", "acme/two");
        await LikeAsync("Third", "acme/three");
        var (job, github, _) = Build(path => path.Contains("/acme/blocked", StringComparison.Ordinal) ? StubHandler.Json("{}", failure) : Ok(path));

        Assert.Equal(2, await job.SnapshotAsync(default));
        Assert.Equal(["acme/two", "acme/three"], (await RowsAsync()).Select(r => r.Repo));
        Assert.Equal(5, github.Calls.Count);   // the blocked repo once, then the two others

        Assert.Equal(0, await job.SnapshotAsync(default));
        Assert.Equal(6, github.Calls.Count);   // asked a second time

        Assert.Equal(1, await job.SnapshotAsync(default));   // the third refusal gives it up: a row with the repo and no stars
        var blocked = (await RowsAsync()).Single(r => r.Repo == "acme/blocked");
        Assert.Null(blocked.Stars);
        Assert.Equal(7, github.Calls.Count);

        Assert.Equal(0, await job.SnapshotAsync(default));
        Assert.Equal(7, github.Calls.Count);   // and never asked again
    }

    [Fact]
    public async Task A_refused_release_call_stores_nothing_for_that_repo_because_the_tag_would_be_wrong()
    {
        await LikeAsync("First", "acme/one");
        var (job, _, _) = Build(path => path.EndsWith("/releases/latest", StringComparison.Ordinal) ? RateLimited(HttpStatusCode.Forbidden) : StubHandler.Json(Repo));

        Assert.Equal(0, await job.SnapshotAsync(default));

        Assert.Empty(await RowsAsync());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection")]
    public async Task A_timeout_or_a_dead_connection_ends_the_run_without_an_error(string failure)
    {
        await LikeAsync("First", "acme/one");
        var calls = 0;
        var time = new FakeTimeProvider(Now);
        var options = Options.Create(new FeedEaterOptions { GitHub = new GitHubOptions { User = "octocat" } });
        var stub = new StubHandler((_, _) =>
        {
            calls++;
            throw failure == "timeout" ? new TaskCanceledException("timed out", new TimeoutException()) : new HttpRequestException("Connection refused");
        });
        var job = new RepoSnapshotJob(
            new GitHubStarsClient(stub.Client("http://github/"), options), new AutopsyStore(pg.Db), new CursorStore(pg.Db), options,
            new LoopHealth(time), time, NullLogger<RepoSnapshotJob>.Instance);

        Assert.Equal(0, await job.SnapshotAsync(default));

        Assert.Equal(1, calls);
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task An_answer_this_cannot_read_skips_that_repo_and_the_others_still_get_their_snapshot()
    {
        await LikeAsync("Broken", "acme/broken");
        await LikeAsync("Fine", "acme/fine");
        var (job, _, _) = Build(path => path.Contains("/acme/broken", StringComparison.Ordinal) ? StubHandler.Json("""{"full_name":"acme/broken"}""") : Ok(path));

        Assert.Equal(1, await job.SnapshotAsync(default));

        Assert.Equal(["acme/fine"], (await RowsAsync()).Select(r => r.Repo));
    }
}
