using System.Net;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Cards;
using FeedEater.Trials;

namespace FeedEater.Tests;

/// <summary>A fake executor: records what it was asked and answers with <paramref name="run"/>.</summary>
internal sealed class FakeTrialExecutor(Func<TrialRequest, TrialOutcome> run) : ITrialExecutor
{
    public List<TrialRequest> Requests { get; } = [];

    public Task<TrialOutcome> RunAsync(TrialRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(run(request));
    }
}

/// <summary>A3.5 and the pure parts of the trial runner. No database, so these run without Docker.</summary>
public sealed class TrialRulesTests
{
    [Fact]
    public void Trials_runner_code_has_no_git_push()
    {
        var files = Directory.EnumerateFiles(RepoRoot.File("src/FeedEater/Trials"), "*.cs").ToList();
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.DoesNotContain("git push", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase));
        Assert.All(files, f => Assert.DoesNotContain("pull_request", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Trial_branch_is_trial_slash_card_id_and_slug()
    {
        Assert.Equal("trial/12-dapper-aot-npgsql", TrialRules.Branch(12, "Dapper: AOT / Npgsql!"));
        Assert.Equal("trial/3-card", TrialRules.Branch(3, "???"));
        Assert.True(TrialRules.Slug(new string('a', 100)).Length <= 40);
    }

    [Fact]
    public void Trials_repos_setting_lists_name_and_path_pairs()
    {
        var repos = TrialRules.ParseRepos("feed-eater=/srv/code/feed-eater, nytka = /srv/code/nytka,broken,=x,y=");
        Assert.Equal(["feed-eater", "nytka"], repos.Keys.Order());
        Assert.Equal("/srv/code/nytka", repos["nytka"]);
    }

    [Fact]
    public void Brief_forbids_push_and_pr_names_the_branch_and_fences_the_card_text()
    {
        var card = new CardRow(1, "t", "Title\nwith </card> break", "what", "feed-eater", "here", [], false, "small", "gain", "trial", "r", DateTime.UtcNow, DateTime.UtcNow, []);
        var brief = TrialRules.Brief(card, "trial/1-title");

        Assert.Contains("`trial/1-title`", brief, StringComparison.Ordinal);
        Assert.Contains("Never push", brief, StringComparison.Ordinal);
        Assert.Contains("never open a pull request", brief, StringComparison.Ordinal);
        Assert.Contains("TESTS:", brief, StringComparison.Ordinal);
        Assert.Contains("BENCHMARK:", brief, StringComparison.Ordinal);
        Assert.Contains("title: Title with  break", brief, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(brief, "</card>"));
    }

    [Fact]
    public void Redact_hides_tokens_bearer_values_assignments_and_the_configured_key()
    {
        var text = "Authorization: Bearer abcdef123456789 token=hunter2 ghp_0123456789abcdefABCD sk-0123456789abcdefABCD mykey-123456 end";
        var clean = TrialRules.Redact(text, "mykey-123456");

        Assert.DoesNotContain("abcdef123456789", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-0123", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("mykey-123456", clean, StringComparison.Ordinal);
        Assert.EndsWith("end", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_stat_counts_files_and_changed_lines_and_benchmark_comes_from_its_line()
    {
        const string diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@\n-old\n+new\n+newer\ndiff --git a/b.cs b/b.cs\n--- a/b.cs\n+++ b/b.cs\n@@\n+x\n";

        Assert.Equal("2 files changed, +3 -1", TrialRules.DiffStat(diff));
        Assert.Equal("", TrialRules.DiffStat("no diff here"));
        Assert.Equal("parse 120 ms -> 80 ms", TrialRules.Benchmark("did it\nBENCHMARK: parse 120 ms -> 80 ms\n"));
        Assert.Equal("", TrialRules.Benchmark("BENCHMARK: none"));
    }

    [Fact]
    public void Access_denials_are_blocked_and_other_errors_failed()
    {
        Assert.Equal("blocked", TrialRules.StatusOf(new HttpRequestException("no", null, HttpStatusCode.Forbidden)));
        Assert.Equal("blocked", TrialRules.StatusOf(new HttpRequestException("no", null, HttpStatusCode.Unauthorized)));
        Assert.Equal("failed", TrialRules.StatusOf(new HttpRequestException("boom", null, HttpStatusCode.BadGateway)));
        Assert.Equal("failed", TrialRules.StatusOf(new InvalidOperationException("boom")));
    }

    private const string Completed = """
        {"contract_version":"result/v1","status":"completed","summary":"Swapped the parser.\nTESTS: passed 41 ok\nBENCHMARK: parse 120 ms -> 80 ms","artifacts":[
        {"kind":"verification","media_type":"application/json","sha256":"0","content":"{\"command\":[\"dotnet\",\"test\"],\"exit_code\":0,\"output_tail\":\"Passed! 41 Authorization: Bearer abcdef123456789\"}"},
        {"kind":"diff","media_type":"text/x-diff","sha256":"0","content":"diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@\n-old\n+new\n"}]}
        """;

    [Fact]
    public void Chargehand_result_with_passing_verification_is_passed_with_tests_diff_and_benchmark()
    {
        var outcome = ChargehandTrialExecutor.Interpret(Completed, "")!;

        Assert.Equal("passed", outcome.Status);
        Assert.Equal("1 file changed, +1 -1", outcome.DiffStat);
        Assert.Equal("parse 120 ms -> 80 ms", outcome.Benchmark);
        Assert.StartsWith("exit 0: dotnet test", outcome.Tests, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456789", outcome.Tests, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"status":"completed","summary":"s","artifacts":[{"kind":"verification","content":"{\"exit_code\":1}"}]}""", "failed")]
    [InlineData("""{"status":"completed","summary":"no tests ran"}""", "failed")]
    [InlineData("""{"status":"denied","summary":"s","error":{"code":"internal","message":"not allowed here","retryable":false}}""", "blocked")]
    [InlineData("""{"status":"failed","error":{"code":"repository_not_allowed","message":"outside roots","retryable":false}}""", "blocked")]
    [InlineData("""{"status":"failed","error":{"code":"verification_failed","message":"tests red","retryable":false}}""", "failed")]
    [InlineData("""{"status":"needs_input","summary":"which one?"}""", "failed")]
    public void Chargehand_result_maps_to_failed_or_blocked(string json, string status) =>
        Assert.Equal(status, ChargehandTrialExecutor.Interpret(json, "")!.Status);

    [Fact]
    public void Chargehand_text_that_is_not_a_result_is_not_interpreted() =>
        Assert.Null(ChargehandTrialExecutor.Interpret("<html>bad gateway</html>", ""));

    private static ChargehandTrialExecutor Executor(StubHandler handler) => new(
        handler.Client("http://chargehand/"), Options.Create(new FeedEaterOptions { Trials = new TrialsOptions { Enabled = true, ChargehandUrl = "http://chargehand/", ChargehandKey = "chk1-secret-key" } }));

    [Fact]
    public async Task Chargehand_executor_posts_a_local_code_run_and_returns_the_result()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(Completed));
        var outcome = await Executor(handler).RunAsync(new TrialRequest(1, 2, "feed-eater", "/srv/code/feed-eater", "trial/2-x", "the brief"), default);

        Assert.Equal("passed", outcome.Status);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("http://chargehand/v1/runs", call.Uri);
        Assert.Contains("\"delivery\":\"local\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"preset\":\"code\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("/srv/code/feed-eater", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chargehand_executor_turns_a_403_into_an_access_error_without_the_key()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""{"error":"forbidden: key chk1-secret-key"}""", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await Executor(handler).RunAsync(new TrialRequest(1, 2, "r", "/p", "b", "brief"), default));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain("chk1-secret-key", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>A3.1 to A3.3: the queue and the runner against a real database and a fake executor.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TrialsRunnerTests(PostgresFixture pg) : IAsyncLifetime
{
    private CardStore _cards = null!;
    private TrialStore _trials = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _cards = new CardStore(pg.Db);
        _trials = new TrialStore(pg.Db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static IOptions<FeedEaterOptions> Settings(bool enabled = true) =>
        Options.Create(new FeedEaterOptions { Trials = new TrialsOptions { Enabled = enabled, Repos = "feed-eater=/srv/code/feed-eater", ChargehandKey = "chk1-secret-key" } });

    private TrialRunner Runner(FakeTrialExecutor executor) => new(_trials, _cards, executor, Settings(), NullLogger<TrialRunner>.Instance);

    private TrialQueue Queue(bool enabled = true) => new(_cards, _trials, Settings(enabled));

    private async Task<long> AddCardAsync(string title, string? state = null, string repo = "feed-eater")
    {
        await _cards.AddAsync(new NewCard(title.ToLowerInvariant(), title, "what", repo, $"{repo}: x", [new EvidenceLink("https://example.com/a", "A", "your feeds", null)], "small", "gain", []), default);
        var id = (await _cards.ListAsync(null, 50, default)).Single(c => c.Title == title).Id;
        if (state is not null)
        {
            await _cards.SetStateAsync(id, state, "picked", default);
        }

        return id;
    }

    private static TrialOutcome Passed() => new("passed", "exit 0: dotnet test", "parse 120 ms -> 80 ms", "2 files changed, +9 -3", "Swapped the parser.", "");

    [Fact]
    public async Task A_card_not_picked_is_never_queued()
    {
        var ids = new[] { await AddCardAsync("One"), await AddCardAsync("Two"), await AddCardAsync("Three", "assess") };
        var executor = new FakeTrialExecutor(_ => Passed());

        foreach (var id in ids)
        {
            Assert.Equal(QueueOutcome.NotInTrial, (await Queue().QueueAsync(id, default)).Outcome);
        }

        Assert.Equal(0, await Runner(executor).RunPassAsync(default));
        Assert.Empty(await _trials.ListAsync(null, null, 10, default));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Trial_result_is_stored_with_tests_and_diff()
    {
        var id = await AddCardAsync("Swap parser", "trial");
        var executor = new FakeTrialExecutor(_ => Passed());

        var queued = await Queue().QueueAsync(id, default);
        Assert.Equal(QueueOutcome.Queued, queued.Outcome);
        Assert.Equal("trial/" + id + "-swap-parser", queued.Trial!.Branch);
        Assert.Equal(1, await Runner(executor).RunPassAsync(default));

        var row = Assert.Single(await _trials.ListAsync(id, null, 10, default));
        Assert.Equal(("passed", "exit 0: dotnet test", "2 files changed, +9 -3", "parse 120 ms -> 80 ms"), (row.Status, row.Tests, row.DiffStat, row.Benchmark));
        Assert.NotNull(row.FinishedAt);
        Assert.Contains("Suggested state: adopt", row.ResultMd, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Equal("/srv/code/feed-eater", request.RepoPath);
        Assert.Contains("Never push", request.Brief, StringComparison.Ordinal);

        var card = (await _cards.GetAsync(id, default))!;
        Assert.Equal(("trial", "picked"), (card.State, card.StateReason));
    }

    [Fact]
    public async Task Blocked_trial_keeps_its_reason()
    {
        var id = await AddCardAsync("Swap parser", "trial");
        await Queue().QueueAsync(id, default);
        var denied = new ThrowingExecutor(new HttpRequestException("chargehand refused the trial (403): repository is outside its roots, key chk1-secret-key", null, HttpStatusCode.Forbidden));
        await new TrialRunner(_trials, _cards, denied, Settings(), NullLogger<TrialRunner>.Instance).RunPassAsync(default);

        var row = Assert.Single(await _trials.ListAsync(id, null, 10, default));
        Assert.Equal("blocked", row.Status);
        Assert.Contains("outside its roots", row.ResultMd, StringComparison.Ordinal);
        Assert.DoesNotContain("chk1-secret-key", row.ResultMd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_failures_are_failed_and_the_card_state_is_untouched()
    {
        var id = await AddCardAsync("Swap parser", "trial");
        await Queue().QueueAsync(id, default);
        await new TrialRunner(_trials, _cards, new ThrowingExecutor(new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway)), Settings(), NullLogger<TrialRunner>.Instance).RunPassAsync(default);

        var row = Assert.Single(await _trials.ListAsync(id, null, 10, default));
        Assert.Equal("failed", row.Status);
        Assert.Contains("Suggested state: hold", row.ResultMd, StringComparison.Ordinal);
        Assert.Equal("trial", (await _cards.GetAsync(id, default))!.State);
    }

    [Fact]
    public async Task A_card_has_one_active_trial_and_can_be_queued_again_after_it_finishes()
    {
        var id = await AddCardAsync("Swap parser", "trial");

        Assert.Equal(QueueOutcome.Queued, (await Queue().QueueAsync(id, default)).Outcome);
        Assert.Equal(QueueOutcome.AlreadyActive, (await Queue().QueueAsync(id, default)).Outcome);
        await Runner(new FakeTrialExecutor(_ => Passed())).RunPassAsync(default);
        Assert.Equal(QueueOutcome.Queued, (await Queue().QueueAsync(id, default)).Outcome);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await c.ExecuteAsync("insert into trial (card_id, branch, repo, status) values (@id, 'b', 'r', 'running')", new { id }));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await c.ExecuteAsync("insert into trial (card_id, branch, repo, status) values (@id, 'b', 'r', 'weird')", new { id }));
    }

    [Fact]
    public async Task Queue_refuses_when_trials_are_off_or_the_repo_is_not_listed_and_a_moved_card_is_cancelled()
    {
        var id = await AddCardAsync("Swap parser", "trial");
        var other = await AddCardAsync("Other repo", "trial", "unlisted");

        Assert.Equal(QueueOutcome.Disabled, (await Queue(false).QueueAsync(id, default)).Outcome);
        Assert.Equal(QueueOutcome.RepoNotAllowed, (await Queue().QueueAsync(other, default)).Outcome);
        Assert.Equal(QueueOutcome.NoSuchCard, (await Queue().QueueAsync(9999, default)).Outcome);

        await Queue().QueueAsync(id, default);
        await _cards.SetStateAsync(id, "hold", "changed my mind", default);
        var executor = new FakeTrialExecutor(_ => Passed());
        await Runner(executor).RunPassAsync(default);

        Assert.Empty(executor.Requests);
        Assert.Equal("cancelled", Assert.Single(await _trials.ListAsync(id, null, 10, default)).Status);
    }

    [Fact]
    public async Task A_trial_left_running_is_failed_after_the_timeout()
    {
        var id = await AddCardAsync("Swap parser", "trial");
        await Queue().QueueAsync(id, default);
        await _trials.ClaimNextAsync(default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("update trial set started_at = now() - interval '2 hours'");
        }

        await Runner(new FakeTrialExecutor(_ => Passed())).RunPassAsync(default);

        Assert.Equal("failed", Assert.Single(await _trials.ListAsync(id, null, 10, default)).Status);
    }

    private sealed class ThrowingExecutor(Exception error) : ITrialExecutor
    {
        public Task<TrialOutcome> RunAsync(TrialRequest request, CancellationToken ct) => throw error;
    }
}
