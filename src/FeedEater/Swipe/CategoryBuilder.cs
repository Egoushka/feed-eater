using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Swipe;

/// <summary>Daily at 03:10 (and at start when today's build is missing): one vector per category in <c>categories.json</c>. A missing file clears the table.</summary>
public sealed class CategoryBuilder(
    CategoryStore categories, LiteLlmClient llm, CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<CategoryBuilder> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "categories";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, new TimeSpan(3, 10, 0));

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var file = CategoriesFile.LoadOrNone(Settings.Swipe.CategoriesPath);
        var rows = new List<CategoryRow>();
        if (file.Categories.Count > 0)
        {
            var vectors = await llm.EmbedAsync(file.Categories.Select(c => c.Description).ToList(), "categories", ct);
            for (var i = 0; i < file.Categories.Count; i++)
            {
                var c = file.Categories[i];
                rows.Add(new CategoryRow
                {
                    Key = c.Key,
                    Description = c.Description,
                    Share = c.Share is { } share ? (float)share : null,
                    Events = c.Events?.ToArray() ?? [],
                    Seed = c.Seed,
                    Embedding = vectors[i].ToArray(),
                });
            }
        }

        await categories.ReplaceAllAsync(rows, Time.GetUtcNow(), ct);
        Logger.LogInformation("Categories rebuilt: {Count}", rows.Count);
    }
}
