using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using FeedEater.PriorArt.Peers;

namespace FeedEater.Mcp;

[McpServerToolType]
public sealed class PeerHarvestTools(PeerHarvestJob job)
{
    [McpServerTool(Name = "peer_harvest", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Fingerprints one peer's dependencies and GitHub counters (no model) and writes a README card (addresses, has_feature, uses; one cheap " +
        "call) into the prior-art namespace. Without repo, runs the configured peer set (PeerHarvest:SetUrl) end to end, one repo at a time. Safe " +
        "to repeat: an unchanged repo stores nothing and spends no model call.")]
    public async Task<string> RunAsync([Description("owner/name; empty runs the configured peer set.")] string? repo = null, CancellationToken ct = default)
    {
        try
        {
            var reports = string.IsNullOrWhiteSpace(repo) ? await job.HarvestSetAsync(ct) : [await job.HarvestAsync(repo.Trim(), ct)];
            return JsonSerializer.Serialize(reports, FeedEater.Json.Options);
        }
        catch (InvalidOperationException e)
        {
            throw new McpProtocolException(e.Message, McpErrorCode.InvalidParams);
        }
        catch (ArgumentException e)
        {
            throw new McpProtocolException(e.Message, McpErrorCode.InvalidParams);
        }
    }
}
