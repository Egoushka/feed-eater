using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using FeedEater.Trials;

namespace FeedEater.Mcp;

[McpServerToolType]
public sealed class TrialTools(TrialQueue queue, TrialStore trials)
{
    [McpServerTool(Name = "feed_trial_run")]
    [Description("Queue a trial of a card you moved to the trial state. It runs on a throwaway branch and never pushes or opens a PR.")]
    public async Task<string> RunAsync([Description("Card id.")] long cardId, CancellationToken ct = default)
    {
        var result = await queue.QueueAsync(cardId, ct);
        return result.Trial is { } row
            ? JsonSerializer.Serialize(row, Json.Options)
            : throw new McpProtocolException(result.Message, McpErrorCode.InvalidParams);
    }

    [McpServerTool(Name = "feed_trials", ReadOnly = true)]
    [Description("Trials with status, tests, benchmark, diff stat and result, newest first.")]
    public async Task<string> TrialsAsync(
        [Description("Only this card.")] long? cardId = null,
        [Description("queued, running, passed, failed, blocked or cancelled; default all.")] string? status = null,
        [Description("1 to 100; default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        if (status is not null && !TrialStatus.All.Contains(status, StringComparer.Ordinal))
        {
            throw new McpProtocolException($"unknown status '{status}'.", McpErrorCode.InvalidParams);
        }

        return JsonSerializer.Serialize(await trials.ListAsync(cardId, status, Math.Clamp(limit, 1, 100), ct), Json.Options);
    }
}
