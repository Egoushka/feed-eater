using Microsoft.Extensions.Options;
using FeedEater.Ranking;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Ui;

namespace FeedEater.Swipe;

public sealed record CardView(long Session, int Position, int Total, string Title, string Feed, string? Category, string Why, string? Url, string Age, bool HasSuggestion);

public sealed record SummaryView(long Session, string Reason, int Cards, int Minutes, IReadOnlyDictionary<string, int> Acts, IReadOnlyList<string> Categories, bool Rated);

public sealed record ClosedView(DateTimeOffset? Until, string Why);

/// <summary>Exactly one of the three is set; <see cref="Type"/> names it for the page.</summary>
public sealed record NextResult(string Type, CardView? Card = null, SummaryView? Summary = null, ClosedView? Closed = null);

public sealed record ActResult(bool Ok, string Message);

internal sealed record Queued(long ItemId, string? Category, string Source, float? Predicted);

/// <summary>Starts, resumes and ends sessions, hands out the cards and applies what the owner does with them. Every id the client names is a position in the session's own queue.</summary>
public sealed class SwipeService(
    SwipeStore store, CategoryStore categories, SwipeYield yield, ProfileStore profiles, FeedbackStore feedback, TasteSwitch tasteSwitch, ItemStore items,
    CallbackHandler callbacks, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<SwipeService> logger)
{
    public static readonly string[] Acts = ["idea", "save", "share", "like", "dislike", "open"];
    private static readonly string[] Directions = ["up", "down", "left", "right", "none"];
    private const int MaxDwellMs = 3_600_000;
    private const double SwipeVoteWeight = 0.5;

    private FeedEaterOptions Settings => options.Value;

    public async Task<NextResult> NextAsync(int? position, CancellationToken ct)
    {
        var o = Settings.Swipe;
        var now = time.GetUtcNow();
        var latest = await store.LatestAsync(ct);
        var decision = SessionPolicy.Decide(o, now, Settings.Zone, latest, await store.StartedSinceAsync(DayStart(now), ct));
        if (decision.Gate == Gate.Expire)
        {
            await store.EndAsync(latest!.Id, "time", SessionPolicy.EndOfExpired(latest), ct);
            latest = await store.LatestAsync(ct);
            decision = SessionPolicy.Decide(o, now, Settings.Zone, latest, await store.StartedSinceAsync(DayStart(now), ct));
        }

        switch (decision.Gate)
        {
            case Gate.Summary:
                return await SummaryResultAsync(latest!, ct);
            case Gate.Closed:
                return new NextResult("closed", Closed: new ClosedView(decision.Until, decision.Why ?? "cooldown"));
            case Gate.Start:
                latest = await StartAsync(now, ct);
                if (latest is null)
                {
                    return new NextResult("closed", Closed: new ClosedView(null, "empty"));
                }

                break;
        }

        return await CardAsync(latest!, position ?? Math.Max(latest!.CardsShown - 1, 0), now, ct);
    }

    private async Task<NextResult> CardAsync(SessionRow s, int position, DateTimeOffset now, CancellationToken ct)
    {
        if (position < 0)
        {
            position = 0;
        }

        if (position >= s.CardCap || position >= s.QueueItems.Length)
        {
            await store.EndAsync(s.Id, s.QueueItems.Length >= s.CardCap ? "cards" : "exhausted", now, ct);
            return await SummaryResultAsync((await store.GetAsync(s.Id, ct))!, ct);
        }

        var item = await items.GetAsync(s.QueueItems[position], ct);
        if (item is null)
        {
            // The item was deleted since the queue was built: say nothing, move on.
            await store.RaiseShownAsync(s.Id, position + 1, ct);
            return await CardAsync(s, position + 1, now, ct);
        }

        await store.RaiseShownAsync(s.Id, position + 1, ct);
        var category = s.QueueCategories.ElementAtOrDefault(position);
        return new NextResult("card", Card: new CardView(
            s.Id, position, Math.Min(s.CardCap, s.QueueItems.Length), item.Title, item.Feed, string.IsNullOrEmpty(category) ? null : category,
            Why(item), Html.SafeUrl(item.Url), Age(item.PublishedAt, now), item.Suggestion is not null));
    }

    private async Task<SessionRow?> StartAsync(DateTimeOffset now, CancellationToken ct)
    {
        var queue = await BuildQueueAsync(now, ct);
        if (queue.Count == 0)
        {
            return null;
        }

        var o = Settings.Swipe;
        var id = await store.StartAsync(o.Cards, (int)o.Cap.TotalSeconds, queue.Select(q => q.Id).ToArray(), queue.Select(q => q.Category ?? "").ToArray(),
            queue.Select(q => q.Source).ToArray(), queue.Select(q => q.Predicted is { } p ? (float)p : float.NaN).ToArray(), now, ct);
        logger.LogInformation("Swipe session {Session} started with {Count} cards", id, queue.Count);
        return await store.GetAsync(id, ct);
    }

    internal async Task<IReadOnlyList<QueuedCard>> BuildQueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var o = Settings;
        var candidates = await store.CandidatesAsync(now.AddDays(-o.Swipe.CandidateDays), now.AddDays(-o.Swipe.RepeatDays), ct);
        if (candidates.Count == 0)
        {
            return [];
        }

        var profileList = await profiles.AllAsync(ct);
        var taste = Taste.Build(
            await feedback.PositiveWeightedAsync(o.Caps.Centroid, ct), await feedback.NegativeWeightedAsync(o.Caps.Centroid, ct),
            await feedback.FeedVotesAsync(ct), o.Weights);
        if (await tasteSwitch.IsOnAsync(ct))
        {
            taste = taste with { Learned = LearnedTaste.Prepare(await feedback.LabeledVectorsAsync(ct), o.Taste).Model };
        }

        var cats = await categories.AllAsync(ct);
        var model = await yield.ModelAsync(ct);
        var scored = candidates.Select(c =>
        {
            var score = Scorer.Score(c.Id, c.FeedId, c.Embedding, profileList, taste, o.Weights).Score;
            double? predicted = model?.Probability(c.Embedding);
            return new QueueCandidate(
                c.Id, predicted is { } p ? score + (o.Swipe.Yield * (p - 0.5)) : score, NearestCategory(c.Embedding, cats, o.Swipe.CategoryFloor), c.Embedding, predicted);
        }).ToList();
        var shares = cats.Where(c => c.Share is > 0).ToDictionary(c => c.Key, c => (double)c.Share!.Value, StringComparer.Ordinal);
        return SwipeQueue.Build(scored, shares, o.Swipe.Cards, Random.Shared.Next(), o.Swipe.Epsilon);
    }

    private static string? NearestCategory(float[] x, IReadOnlyList<CategoryRow> cats, double floor)
    {
        string? key = null;
        var best = floor;
        foreach (var c in cats)
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

    public async Task<ActResult> ActAsync(long sessionId, int position, string act, CancellationToken ct)
    {
        var queued = Acts.Contains(act, StringComparer.Ordinal) ? await QueuedAsync(sessionId, position, ct) : null;
        if (queued is null)
        {
            return new ActResult(false, "Nothing to do.");
        }

        var (itemId, category, source, predicted) = queued;

        var message = act switch
        {
            "like" => await VoteAsync(itemId, 1, SwipeVoteWeight, "Liked", ct),
            "dislike" => await VoteAsync(itemId, -1, SwipeVoteWeight, "Not for me", ct),
            "save" => await SaveAsync(itemId, ct),
            "idea" => await IdeaAsync(itemId, ct),
            _ => "",
        };
        if (message is null)
        {
            return new ActResult(false, "Could not do that.");
        }

        var existing = await store.EventAsync(sessionId, position, ct) ?? new EventRow { SessionId = sessionId, Position = position, RefId = itemId, Category = category, RankSource = source, Predicted = predicted };
        var acts = existing.Acts.Append(act).Distinct(StringComparer.Ordinal).ToArray();
        await store.SaveEventAsync(existing with { Acts = acts, Action = Primary(acts, existing.Direction) }, ct);
        return new ActResult(true, message);
    }

    /// <summary>What happened to a card when it was left (or the page was hidden): the direction, the dwell and the time it was shown. Idempotent; the last call wins.</summary>
    public async Task<bool> EventAsync(long sessionId, int position, long? shownAtMs, long? dwellMs, string? direction, CancellationToken ct)
    {
        if (await QueuedAsync(sessionId, position, ct) is not { } queued)
        {
            return false;
        }

        var (itemId, category, source, predicted) = queued;

        var existing = await store.EventAsync(sessionId, position, ct) ?? new EventRow { SessionId = sessionId, Position = position, RefId = itemId, Category = category, RankSource = source, Predicted = predicted };
        var dir = direction is not null && Directions.Contains(direction, StringComparer.Ordinal) ? direction : "none";
        var shown = shownAtMs is > 0 && DateTimeOffset.FromUnixTimeMilliseconds(shownAtMs.Value) is var at && Math.Abs((at - time.GetUtcNow()).TotalDays) < 1 ? at.UtcDateTime : (DateTime?)null;
        await store.SaveEventAsync(existing with
        {
            Direction = dir,
            ShownAt = shown,
            DwellMs = dwellMs is null ? null : (int)Math.Clamp(dwellMs.Value, 0, MaxDwellMs),
            Action = Primary(existing.Acts, dir),
        }, ct);
        return true;
    }

    public async Task<bool> FeltAsync(long sessionId, int felt, CancellationToken ct) =>
        felt is -1 or 0 or 1 && await store.SetFeltAsync(sessionId, (short)felt, time.GetUtcNow(), ct);

    /// <summary>The "I'm done" button: ends the open session and returns its summary.</summary>
    public async Task<NextResult?> EndAsync(long sessionId, CancellationToken ct)
    {
        if (await store.GetAsync(sessionId, ct) is not { } s)
        {
            return null;
        }

        if (s.EndedAt is null)
        {
            await store.EndAsync(s.Id, "user", time.GetUtcNow(), ct);
        }

        return await SummaryResultAsync((await store.GetAsync(sessionId, ct))!, ct);
    }

    private async Task<Queued?> QueuedAsync(long sessionId, int position, CancellationToken ct)
    {
        var s = await store.GetAsync(sessionId, ct);
        if (s is null || position < 0 || position >= s.CardsShown || position >= s.QueueItems.Length)
        {
            return null;
        }

        var category = s.QueueCategories.ElementAtOrDefault(position);
        var source = s.QueueSources.ElementAtOrDefault(position) ?? SwipeQueue.Exploit;
        var predicted = s.QueuePredicted.Length > position && !float.IsNaN(s.QueuePredicted[position]) ? s.QueuePredicted[position] : (float?)null;
        return new Queued(s.QueueItems[position], string.IsNullOrEmpty(category) ? null : category, source, predicted);
    }

    private async Task<string?> VoteAsync(long itemId, short value, double weight, string message, CancellationToken ct)
    {
        await feedback.SetVoteAsync(itemId, value, ct, weight);
        return message;
    }

    /// <summary>A save is a firm yes (weight 1) and, when Karakeep is set up, a bookmark. Karakeep being off or down does not undo the save.</summary>
    private async Task<string> SaveAsync(long itemId, CancellationToken ct)
    {
        await feedback.SetVoteAsync(itemId, 1, ct);
        var outcome = await callbacks.SaveAsync(itemId, ct);
        return outcome is SaveOutcome.Saved or SaveOutcome.AlreadySaved or SaveOutcome.NotConfigured or SaveOutcome.Refused
            ? "Saved"
            : "Saved here; Karakeep is not reachable";
    }

    private async Task<string?> IdeaAsync(long itemId, CancellationToken ct)
    {
        var (project, error) = await callbacks.FileIdeaAsync(itemId, ct);
        return project is null ? null : error ?? $"Idea filed in {project}";
    }

    private async Task<NextResult> SummaryResultAsync(SessionRow s, CancellationToken ct)
    {
        var data = await store.SummaryAsync(s, time.GetUtcNow(), ct);
        return new NextResult("summary", Summary: new SummaryView(
            s.Id, s.EndReason ?? "user", data.CardsShown, (int)Math.Round(data.Minutes), data.Acts, data.Categories.Select(c => c.Category).ToList(), s.FeltAt is not null));
    }

    /// <summary>The strongest thing done with the card, else where it was swiped.</summary>
    internal static string Primary(IReadOnlyCollection<string> acts, string? direction) =>
        Acts.FirstOrDefault(acts.Contains) ?? (direction == "down" ? "back" : "next");

    private DateTimeOffset DayStart(DateTimeOffset now)
    {
        var zone = Settings.Zone;
        var day = TimeZoneInfo.ConvertTime(now, zone).Date;
        return new DateTimeOffset(day, zone.GetUtcOffset(day));
    }

    private static string Why(ItemView item)
    {
        var text = item.Why ?? item.Reason;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = item.Content.Length <= 280 ? item.Content : item.Content[..279] + "…";
        }

        return text;
    }

    private static string Age(DateTime published, DateTimeOffset now)
    {
        var age = now - new DateTimeOffset(DateTime.SpecifyKind(published, DateTimeKind.Utc));
        return age.TotalHours < 1 ? "just now" : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} d ago";
    }
}
