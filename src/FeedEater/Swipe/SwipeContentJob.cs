using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Profiles;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Swipe;

public static class Categorizer
{
    /// <summary>The key of the category whose vector is nearest to <paramref name="x"/>, or null when none reaches <paramref name="floor"/>.</summary>
    public static string? Nearest(float[] x, IReadOnlyList<CategoryRow> categories, double floor)
    {
        string? key = null;
        var best = floor;
        foreach (var c in categories)
        {
            var s = Vectors.Dot(x, c.Embedding);
            if (s >= best)
            {
                best = s;
                key = c.Key;
            }
        }

        return key;
    }
}

/// <summary>
/// Every ten minutes: turns items you liked into recall questions and post seeds (small model, capped per day), and your notes into recall cards (no model call
/// except one embedding to place them in a category). The swipe path itself calls no model.
/// </summary>
public sealed class SwipeContentJob(
    RecallStore recall, CategoryStore categories, LiteLlmClient llm, IServiceProvider services, IOptions<FeedEaterOptions> options, LoopHealth health,
    TimeProvider time, ILogger<SwipeContentJob> logger) : PollingLoop(health, time, logger)
{
    private const int NotesPerPoll = 20;
    private static readonly TimeSpan SeedWindow = TimeSpan.FromDays(3);

    internal Task TickAsync(CancellationToken ct) => PollAsync(ct);

    protected override string Name => "swipe content";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);

    protected override async Task PollAsync(CancellationToken ct)
    {
        var o = options.Value;
        var cats = await categories.AllAsync(ct);
        if (o.Swipe.Recall.Enabled)
        {
            await ItemCardsAsync(o, cats, ct);
            await NoteCardsAsync(o, cats, ct);
        }

        if (o.Swipe.Seeds.Enabled)
        {
            await SeedsAsync(o, cats, ct);
        }
    }

    private async Task ItemCardsAsync(FeedEaterOptions o, IReadOnlyList<CategoryRow> cats, CancellationToken ct)
    {
        var now = Time.GetUtcNow();
        var left = o.Swipe.Recall.PerDay - await recall.ItemsDoneSinceAsync(DayStart(o, now), ct);
        if (left <= 0)
        {
            return;
        }

        foreach (var item in await recall.LikedWithoutCardsAsync(left, ct))
        {
            var (system, user) = SwipeContent.Recall(item.Title, item.Summary, item.Content, o.Caps.ReadChars);
            var reply = await llm.ChatAsync(o.Llm.TriageModel, system, user, 500, "swipe-recall", ct);
            var pairs = SwipeContent.ParseCards(reply.Content);
            await recall.AddCardsAsync("item", $"item:{item.Id}", item.Id, Categorizer.Nearest(item.Embedding, cats, o.Swipe.CategoryFloor), pairs, now, ct);
            Logger.LogInformation("Recall: {Count} cards from item {Item}", pairs.Count, item.Id);
        }
    }

    private async Task NoteCardsAsync(FeedEaterOptions o, IReadOnlyList<CategoryRow> cats, CancellationToken ct)
    {
        if (!o.Swipe.Notes.Enabled)
        {
            return;
        }

        var notes = await services.GetRequiredService<NotesClient>().ListAsync(ct);
        var done = await recall.DoneAmongAsync(notes.Select(n => $"note:{n.Id}").ToList(), ct);
        var fresh = notes.Where(n => !done.Contains($"note:{n.Id}")).Take(NotesPerPoll).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var pairs = fresh.Select(n => SwipeContent.FromNote(n.Topic, n.ConversationTitle, n.Points)).ToList();
        var vectors = cats.Count == 0 ? null : await llm.EmbedAsync(pairs.Select(p => p.Question + " " + p.Answer).ToList(), "swipe-notes", ct);
        var now = Time.GetUtcNow();
        for (var i = 0; i < fresh.Count; i++)
        {
            var category = vectors is null ? null : Categorizer.Nearest(vectors[i], cats, o.Swipe.CategoryFloor);
            await recall.AddCardsAsync("note", $"note:{fresh[i].Id}", null, category, [pairs[i]], now, ct);
        }

        Logger.LogInformation("Recall: {Count} cards from notes", fresh.Count);
    }

    private async Task SeedsAsync(FeedEaterOptions o, IReadOnlyList<CategoryRow> cats, CancellationToken ct)
    {
        var now = Time.GetUtcNow();
        var left = o.Swipe.Seeds.PerDay - await recall.SeedsCreatedSinceAsync(DayStart(o, now), ct);
        if (left <= 0)
        {
            return;
        }

        var seedable = cats.Where(c => c.Seed).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var about = ProfileFile.LoadOrExample(o.ProfilePath).File.About;
        foreach (var item in await recall.LikedWithoutSeedAsync(now - SeedWindow, left * 4, ct))
        {
            if (left <= 0)
            {
                break;
            }

            // With no category flagged for seeds every liked item can seed; otherwise only items in a flagged category.
            var category = Categorizer.Nearest(item.Embedding, cats, o.Swipe.CategoryFloor);
            if (seedable.Count > 0 && (category is null || !seedable.Contains(category)))
            {
                continue;
            }

            var (system, user) = SwipeContent.Seed(about, item.Title, item.Summary, item.Why);
            var reply = await llm.ChatAsync(o.Llm.TriageModel, system, user, 150, "swipe-seed", ct);
            var angle = SwipeContent.ParseAngle(reply.Content) ?? "";
            await recall.AddSeedAsync(item.Id, angle, category, ct);
            if (angle.Length > 0)
            {
                left--;
            }
        }
    }

    private static DateTimeOffset DayStart(FeedEaterOptions o, DateTimeOffset now)
    {
        var day = TimeZoneInfo.ConvertTime(now, o.Zone).Date;
        return new DateTimeOffset(day, o.Zone.GetUtcOffset(day));
    }
}
