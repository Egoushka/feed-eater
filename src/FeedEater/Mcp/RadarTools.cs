using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using FeedEater.Radar;

namespace FeedEater.Mcp;

[McpServerToolType]
public sealed class RadarTools(RadarExporter exporter)
{
    [McpServerTool(Name = "feed_radar_export")]
    [Description("Export personal cards in assess, trial, adopt or hold to radar.json and open or update the site's pull request. Never merges. Off unless Radar:Enabled.")]
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        try
        {
            return JsonSerializer.Serialize(await exporter.ExportAsync(ct), Json.Options);
        }
        catch (RadarExportException ex)
        {
            throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
        }
    }
}
