using Microsoft.Extensions.Options;
using FeedEater.Cards;

namespace FeedEater.Trials;

public enum QueueOutcome { Queued, Disabled, NoSuchCard, NotInTrial, RepoNotAllowed, AlreadyActive }

public sealed record QueueResult(QueueOutcome Outcome, TrialRow? Trial)
{
    public string Message => Outcome switch
    {
        QueueOutcome.Queued => "queued.",
        QueueOutcome.Disabled => "trials are off: set Trials:Enabled.",
        QueueOutcome.NoSuchCard => "no such card.",
        QueueOutcome.NotInTrial => "only a card you moved to the trial state can be queued.",
        QueueOutcome.RepoNotAllowed => "the card's repo is not in Trials:Repos.",
        _ => "this card already has a trial queued or running.",
    };
}

/// <summary>The one way a trial gets queued, from the card page or the MCP tool. Both are the owner's hand: the card generator never calls it.</summary>
public sealed class TrialQueue(CardStore cards, TrialStore trials, IOptions<FeedEaterOptions> options)
{
    public async Task<QueueResult> QueueAsync(long cardId, CancellationToken ct)
    {
        var settings = options.Value.Trials;
        if (!settings.Enabled)
        {
            return new QueueResult(QueueOutcome.Disabled, null);
        }

        if (await cards.GetAsync(cardId, ct) is not { } card)
        {
            return new QueueResult(QueueOutcome.NoSuchCard, null);
        }

        // A state that was never set by a person has no state_changed_at, so a generated card cannot pass.
        if (card.State != "trial" || card.StateChangedAt is null)
        {
            return new QueueResult(QueueOutcome.NotInTrial, null);
        }

        if (!TrialRules.ParseRepos(settings.Repos).ContainsKey(card.Repo))
        {
            return new QueueResult(QueueOutcome.RepoNotAllowed, null);
        }

        return await trials.QueueAsync(card.Id, card.Repo, TrialRules.Branch(card.Id, card.Title), ct) is { } row
            ? new QueueResult(QueueOutcome.Queued, row)
            : new QueueResult(QueueOutcome.AlreadyActive, null);
    }
}
