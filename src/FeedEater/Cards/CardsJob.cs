using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Cards;

/// <summary>Daily at <c>Cards:At</c>: read the inventory of the owner's repos, then write cards from it.</summary>
public sealed class CardsJob(
    InventoryCollector collector, CardGenerator generator, CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<CardsJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "cards";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, Settings.Cards.At);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        await collector.CollectAsync(ct);
        await generator.GenerateAsync(ct);
    }
}
