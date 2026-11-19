using Microsoft.Extensions.Options;
using FeedEater.Cards;
using FeedEater.Trials;

namespace FeedEater.Radar;

/// <summary>Reads the cards, builds radar.json and hands it to the site repo as a pull request.</summary>
public sealed class RadarExporter(CardStore cards, TrialStore trials, RadarPublisher publisher, IOptions<FeedEaterOptions> options)
{
    public async Task<RadarPublished> ExportAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Radar.Enabled)
        {
            throw new RadarExportException("the radar export is off: set FeedEater:Radar:Enabled");
        }

        if (o.Cards.Root.Length == 0 || !Directory.Exists(o.Cards.Root))
        {
            throw new RadarExportException("Cards:Root is not set or does not exist; there is no manifest to tell personal repos from work ones");
        }

        var root = InventoryCollector.RealPath(Path.GetFullPath(o.Cards.Root));
        var manifestText = InventoryCollector.ReadText(root, Path.Combine(root, o.Cards.Manifest))
            ?? throw new RadarExportException("the repo manifest is not readable; nothing is exported");
        var all = await cards.ListAsync(null, 1000, ct);
        var results = new Dictionary<long, string>();
        foreach (var card in all.Where(c => c.State is "trial" or "adopt" or "hold"))
        {
            var done = (await trials.ListAsync(card.Id, null, 10, ct)).FirstOrDefault(t => t.Status is "passed" or "failed");
            if (done is { ResultMd.Length: > 0 })
            {
                results[card.Id] = done.ResultMd;
            }
        }

        var json = RadarRules.Build(all, InventoryParsers.Manifest(manifestText), await cards.InventoryAsync(ct), results,
            DenyList.Load(o.Cards.DenyTermsFile));
        return await publisher.PublishAsync(json, ct);
    }
}
