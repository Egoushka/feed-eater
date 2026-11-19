using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using FeedEater.Cards;

namespace FeedEater.Mcp;

[McpServerToolType]
public sealed class CardTools(CardStore cards)
{
    [McpServerTool(Name = "feed_cards", ReadOnly = true)]
    [Description("Adoption cards built from the owner's stack: title, where in the stack, evidence links, effort, gain, state and reason.")]
    public async Task<string> CardsAsync(
        [Description("proposed, assess, trial, adopt, hold or dropped; default all.")] string? state = null,
        [Description("1 to 100; default 30.")] int limit = 30,
        CancellationToken ct = default)
    {
        if (state is not null && !CardRules.States.Contains(state, StringComparer.Ordinal))
        {
            throw new McpProtocolException($"unknown state '{state}'.", McpErrorCode.InvalidParams);
        }

        return JsonSerializer.Serialize(await cards.ListAsync(state, Math.Clamp(limit, 1, 100), ct), Json.Options);
    }

    [McpServerTool(Name = "feed_card_set_state", Idempotent = true)]
    [Description("Move a card to a state: proposed, assess, trial, adopt, hold or dropped. A reason is required.")]
    public async Task<string> SetStateAsync(
        [Description("Card id.")] long id,
        [Description("New state.")] string state,
        [Description("Why, in a sentence.")] string reason,
        CancellationToken ct = default)
    {
        if (CardRules.CheckTransition(state, reason) is { } error)
        {
            throw new McpProtocolException(error, McpErrorCode.InvalidParams);
        }

        return await cards.SetStateAsync(id, state, reason, ct)
            ? JsonSerializer.Serialize(await cards.GetAsync(id, ct), Json.Options)
            : throw new McpProtocolException($"no card {id}.", McpErrorCode.InvalidParams);
    }
}
