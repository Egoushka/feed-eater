using Microsoft.Extensions.Options;
using FeedEater.Cards;
using FeedEater.Loops;

namespace FeedEater.Trials;

/// <summary>Picks queued trials one at a time and runs each through the executor. It only reads cards: a result never changes a card's state.</summary>
public sealed class TrialRunner(TrialStore trials, CardStore cards, ITrialExecutor executor, IOptions<FeedEaterOptions> options, ILogger<TrialRunner> logger)
{
    public async Task<int> RunPassAsync(CancellationToken ct)
    {
        var settings = options.Value.Trials;
        await trials.ExpireStaleAsync(TimeSpan.FromMinutes(settings.TimeoutMinutes + 10), ct);
        var ran = 0;
        while (!ct.IsCancellationRequested && await trials.ClaimNextAsync(ct) is { } trial)
        {
            await RunOneAsync(trial, ct);
            ran++;
        }

        return ran;
    }

    private async Task RunOneAsync(TrialRow trial, CancellationToken ct)
    {
        var settings = options.Value.Trials;
        var key = settings.ChargehandKey;
        var card = await cards.GetAsync(trial.CardId, ct);
        if (card is null || card.State != "trial")
        {
            await FinishAsync(trial, TrialStatus.Cancelled, "the card is no longer in the trial state.", "", "", "", "", ct);
            return;
        }

        if (!TrialRules.ParseRepos(settings.Repos).TryGetValue(trial.Repo, out var path))
        {
            await FinishAsync(trial, TrialStatus.Blocked, $"repo {trial.Repo} is not in Trials:Repos.", "", "", "", "", ct);
            return;
        }

        TrialOutcome outcome;
        try
        {
            outcome = await executor.RunAsync(new TrialRequest(trial.Id, card.Id, trial.Repo, path, trial.Branch, TrialRules.Brief(card, trial.Branch)), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(trial, TrialStatus.Cancelled, "the runner was stopped while this trial ran.", "", "", "", "", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception's type and message are kept, redacted; the brief and the run's text never reach the log.
            var status = TrialRules.StatusOf(ex);
            await FinishAsync(trial, status, TrialRules.Redact(ex.Message, key), "", "", "", "", ct);
            return;
        }

        await FinishAsync(trial, outcome.Status, outcome.Reason, outcome.Summary, outcome.Tests, outcome.Benchmark, outcome.DiffStat, ct);
    }

    private async Task FinishAsync(TrialRow trial, string status, string reason, string summary, string tests, string benchmark, string diffStat, CancellationToken ct)
    {
        var md = TrialRules.ResultMd(status, reason, summary, benchmark);
        await trials.FinishAsync(trial.Id, status, tests, benchmark, diffStat, md, ct);
        logger.LogInformation("Trial {Trial} of card {Card} finished {Status}", trial.Id, trial.CardId, status);
    }
}

/// <summary>Every 30 seconds: run what is queued. Only registered when Trials:Enabled.</summary>
public sealed class TrialsJob(TrialRunner runner, LoopHealth health, TimeProvider time, ILogger<TrialsJob> logger) : PollingLoop(health, time, logger)
{
    protected override string Name => "trials";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);
    protected override async Task PollAsync(CancellationToken ct) => await runner.RunPassAsync(ct);
}
