using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Eval;
using FeedEater.Llm;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class EvalSettingsTests
{
    [Fact]
    public void The_spend_limit_is_required_and_the_other_flags_are_parsed()
    {
        var (none, error) = EvalSettings.Parse([]);
        Assert.Null(none);
        Assert.Contains("--max-usd is required", error, StringComparison.Ordinal);

        var (ok, _) = EvalSettings.Parse(["--max-usd", "0.25", "--max-items", "12", "--reads", "--out", "r.md"]);
        Assert.Equal(new EvalSettings(12, 0.25m, true, "r.md"), ok);
        Assert.Equal(new EvalSettings(40, 1m, false, null), EvalSettings.Parse(["--max-usd", "1"]).Settings);
    }

    [Theory]
    [InlineData("--max-usd", "0")]
    [InlineData("--max-usd", "abc")]
    [InlineData("--max-usd")]
    [InlineData("--surprise")]
    public void Bad_arguments_are_refused_with_the_usage(params string[] args)
    {
        var (settings, error) = EvalSettings.Parse(args);

        Assert.Null(settings);
        Assert.Contains("Usage: eval --max-usd", error, StringComparison.Ordinal);
    }
}

public sealed class EvalReportTests
{
    private static EvalRow Row(long id, int vote, int relevance, string kind = "fyi", string text = "plain", string? summary = null, int? old = null, string? oldKind = null, string? oldSummary = null) =>
        new(id, $"Item {id}", vote, old, oldKind, oldSummary, relevance, kind, summary, text);

    [Fact]
    public void Says_clearly_when_there_are_too_few_votes_and_still_prints_the_numbers()
    {
        var report = EvalReport.Render([Row(1, 1, 3), Row(2, -1, 0)], 1, 1, 2, 0.002m, 0.25m, false, null);

        Assert.Contains("**Too few votes to mean anything.**", report, StringComparison.Ordinal);
        Assert.Contains("Evaluated: 1 up, 1 down", report, StringComparison.Ordinal);
        Assert.Contains("1.00 (0.50 is chance)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void With_enough_votes_it_reports_agreement_the_bar_and_kind_counts_without_the_warning()
    {
        var rows = Enumerable.Range(0, 12).Select(i => Row(i, 1, i < 9 ? 3 : 1, "improve")).Concat(Enumerable.Range(100, 12).Select(i => Row(i, -1, i < 104 ? 2 : 0, "fyi"))).ToList();

        var report = EvalReport.Render(rows, 30, 30, 2, 0.01m, 0.25m, false, null);

        Assert.DoesNotContain("Too few votes", report, StringComparison.Ordinal);
        Assert.Contains("| passes the digest bar (relevance >= 2) | 9 of 12 (75%) | 4 of 12 (33%) |", report, StringComparison.Ordinal);
        Assert.Contains("| improve | 0 | 12 |", report, StringComparison.Ordinal);
        Assert.Contains("| fyi | 0 | 12 |", report, StringComparison.Ordinal);
        Assert.Contains("Largest disagreements", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pairwise_agreement_counts_ties_as_half()
    {
        Assert.Equal("0.50 (0.50 is chance)", EvalReport.Auc([2, 2], [2, 2]));
        Assert.Equal("1.00 (0.50 is chance)", EvalReport.Auc([3], [0, 1]));
        Assert.Equal("0.00 (0.50 is chance)", EvalReport.Auc([0], [3]));
        Assert.StartsWith("n/a", EvalReport.Auc([], [1]), StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_new_claims_that_have_no_version_or_date_to_stand_on_and_flags_novelty_wording()
    {
        var rows = new[]
        {
            Row(1, 1, 2, "new", "A self-hosted dashboard for speed tests. Try it at the site.", "Pingularity is a new dashboard.", old: 2, oldKind: "new", oldSummary: "Pingularity is a new dashboard."),
            Row(2, 1, 2, "new", "Pingularity 2.1.0 released on 2026-09-30 with a new chart."),
            Row(3, -1, 0, "fyi", "Nothing", "A summary.", old: 0, oldKind: "new"),
        };

        var report = EvalReport.Render(rows, 2, 1, 2, 0, 0.25m, reads: true, null);

        Assert.Contains("\"new\" with no version or date in the item's text: 2 of 2 stored, 1 of 2 now.", report, StringComparison.Ordinal);
        Assert.Contains("1 stored, 1 of 2 now.", report, StringComparison.Ordinal);
        Assert.True(EvalReport.HasEvidence("v1.2 shipped") && EvalReport.HasEvidence("in 2026") && !EvalReport.HasEvidence("a tool for speed tests"));
        Assert.True(EvalReport.ClaimsNovelty("Just launched: a tool") && EvalReport.ClaimsNovelty("recently introduced") && !EvalReport.ClaimsNovelty("A dashboard for speed tests."));
    }

    [Fact]
    public void An_empty_run_and_an_early_stop_are_reported()
    {
        var report = EvalReport.Render([], 0, 0, 2, 0, 0.25m, false, "the $0.25 limit was reached after 0 of 0 items");

        Assert.Contains("Stopped early", report, StringComparison.Ordinal);
        Assert.Contains("Nothing was evaluated.", report, StringComparison.Ordinal);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class EvalRunnerTests(PostgresFixture pg) : IAsyncLifetime
{
    private int _calls;
    private string _reply = """{"relevance":3,"project":null,"kind":"new","reason":"r"}""";
    private decimal _cost = 0.01m;
    private bool _sendCost = true;
    private LlmOptions _llm = new();
    private HttpStatusCode _status = HttpStatusCode.OK;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private EvalRunner Build()
    {
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json", Llm = _llm });
        var stub = new StubHandler((_, body) =>
        {
            _calls++;
            var response = StubHandler.Json(
                JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = _reply } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } }), _status);
            if (_sendCost)
            {
                response.Headers.Add("x-litellm-response-cost", _cost.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return response;
        });
        return new EvalRunner(new EvalStore(pg.Db), new ProfileStore(pg.Db), new LiteLlmClient(stub.Client("http://llm/"), new UsageStore(pg.Db), options), options);
    }

    private async Task VoteAsync(int count, short value, string prefix)
    {
        for (var i = 0; i < count; i++)
        {
            var id = await Seed.ItemAsync(pg, 1, $"{prefix} {i}", TestVectors.OneHot(i + (value > 0 ? 0 : 50)), DateTime.UtcNow, content: "text");
            await new FeedbackStore(pg.Db).SetVoteAsync(id, value, default);
        }
    }

    [Fact]
    public async Task Evaluates_a_balanced_sample_with_the_current_prompts_and_warns_when_votes_are_few()
    {
        await VoteAsync(6, 1, "liked");
        await VoteAsync(2, -1, "disliked");

        var report = await Build().RunAsync(new EvalSettings(4, 1m, false, null), default);

        Assert.Equal(4, _calls);
        Assert.Contains("Votes in the archive: 6 up, 2 down. Evaluated: 2 up, 2 down.", report, StringComparison.Ordinal);
        Assert.Contains("**Too few votes to mean anything.**", report, StringComparison.Ordinal);
        Assert.Contains("| new | 0 | 4 |", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_at_the_spend_limit_and_says_so()
    {
        await VoteAsync(5, 1, "liked");
        await VoteAsync(5, -1, "disliked");
        _cost = 0.04m;

        var report = await Build().RunAsync(new EvalSettings(10, 0.10m, false, null), default);

        Assert.Equal(3, _calls);   // 0.04 + 0.04 + 0.04 reaches the 0.10 limit
        Assert.Contains("Stopped early: the $0.10 limit was reached after 3 of 10 items", report, StringComparison.Ordinal);
        Assert.Contains("Spent $0.1200 of $0.10", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_to_run_when_a_call_comes_back_with_no_cost_header_and_no_price()
    {
        await VoteAsync(5, 1, "liked");
        await VoteAsync(5, -1, "disliked");
        _sendCost = false;

        var error = await Assert.ThrowsAsync<CostUnknownException>(() => Build().RunAsync(new EvalSettings(10, 0.10m, false, null), default));

        Assert.Contains("gpt-4.1-nano", error.Message, StringComparison.Ordinal);
        Assert.Contains("FeedEater:Llm:Prices", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, _calls);   // the first call is the only one spent before it can tell
    }

    [Fact]
    public async Task A_configured_price_lets_the_limit_work_without_a_cost_header()
    {
        await VoteAsync(5, 1, "liked");
        await VoteAsync(5, -1, "disliked");
        _sendCost = false;
        _llm = new LlmOptions { Prices = { ["gpt-4.1-nano"] = new ModelPrice { Input = 4_000m } } };   // 10 input tokens cost $0.04

        var report = await Build().RunAsync(new EvalSettings(10, 0.10m, false, null), default);

        Assert.Equal(3, _calls);
        Assert.Contains("Stopped early: the $0.10 limit was reached after 3 of 10 items", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_reads_only_for_items_that_pass_triage_when_asked()
    {
        await VoteAsync(2, 1, "liked");
        var runner = Build();
        _reply = """{"relevance":3,"kind":"new","summary":"A new tool.","why":"w","project":null,"suggestion":null}""";

        var report = await runner.RunAsync(new EvalSettings(2, 1m, true, null), default);

        Assert.Equal(4, _calls);   // a triage and a read per item
        Assert.Contains("Summaries that call something new", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_outage_ends_the_run_with_a_note_instead_of_throwing()
    {
        await VoteAsync(2, 1, "liked");
        _status = HttpStatusCode.ServiceUnavailable;

        var report = await Build().RunAsync(new EvalSettings(2, 1m, false, null), default);

        Assert.Contains("the model was unreachable", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sample_alternates_up_and_down_newest_first_and_respects_the_cap()
    {
        var golden = new[] { 1, 2, 3, 4 }.Select(i => new GoldenRow { Id = i, Vote = 1 }).Concat([new GoldenRow { Id = 9, Vote = -1 }]).ToList();

        Assert.Equal([1L, 9, 2, 3, 4], EvalRunner.Balanced(golden, 10).Select(g => g.Id));
        Assert.Equal([1L, 9, 2], EvalRunner.Balanced(golden, 3).Select(g => g.Id));
    }
}
