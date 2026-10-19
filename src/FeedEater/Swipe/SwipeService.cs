using System.Text.Json.Nodes;
using FeedEater.Inbox;
using FeedEater.Library;
using Microsoft.Extensions.Options;
using FeedEater.Ranking;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Ui;

namespace FeedEater.Swipe;

public sealed record CardView(
    long Session, int Position, int Total, string Title, string Feed, string? Category, string Why, string? Url, string Age, bool HasSuggestion,
    string Kind = "item", string? Back = null, IReadOnlyDictionary<string, string?>? Detail = null)
{
    /// <summary>The acts the card takes, so a client need not know them per kind.</summary>
    public IReadOnlyList<string> Acts { get; init; } = [];
}

public sealed record SummaryView(long Session, string Reason, int Cards, int Minutes, IReadOnlyDictionary<string, int> Acts, IReadOnlyList<string> Categories, bool Rated);

public sealed record ClosedView(DateTimeOffset? Until, string Why);

/// <summary>Exactly one of the three is set; <see cref="Type"/> names it for the page.</summary>
public sealed record NextResult(string Type, CardView? Card = null, SummaryView? Summary = null, ClosedView? Closed = null);

public sealed record ActResult(bool Ok, string Message);

internal sealed record Queued(string Kind, long RefId, string? Category, string Source, float? Predicted);

/// <summary>Starts, resumes and ends sessions, hands out the cards and applies what the owner does with them. Every id the client names is a position in the session's own queue.</summary>
public sealed class SwipeService(
    SwipeStore store, RecallStore recall, CategoryStore categories, SwipeYield yield, ProfileStore profiles, FeedbackStore feedback, TasteSwitch tasteSwitch, ItemStore items,
    InboxStore inbox, InboxCards inboxCards, SwipeActStore acts, FollowStore follows, WeeklyIdeaStore ideas, CallbackHandler callbacks, LibraryService library, LibraryStore libraryStore, IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<SwipeService> logger)
{
    public static readonly string[] Acts = ["idea", "save", "share", "like", "dislike", "open"];
    private static readonly string[] RecallActs = ["good", "again"];
    private static readonly string[] SeedActs = ["send", "dismiss"];
    private static readonly string[] Strongest = [.. Acts, .. RecallActs, .. SeedActs];
    private static readonly string[] Directions = ["up", "down", "left", "right", "none"];
    private const int MaxDwellMs = 3_600_000;
    private static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);
    private const double SwipeVoteWeight = 0.5;

    private FeedEaterOptions Settings => options.Value;

    /// <summary>What <see cref="NextAsync"/> would decide right now, without expiring or starting anything.</summary>
    public async Task<Decision> GateAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return SessionPolicy.Decide(Settings.Swipe, now, Settings.Zone, await store.LatestAsync(ct), await store.StartedSinceAsync(DayStart(now), ct));
    }

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

        return await CardAsync(latest!, position ?? Math.Max(latest!.CardsShown - 1, 0), now, true, ct);
    }

    /// <summary>
    /// A look at the card at <paramref name="position"/> for fetching ahead (ruling R-j). Decides the gate like <see cref="NextAsync"/> but never starts, expires
    /// or ends a session, and does not count the card as shown: the first event for its position does.
    /// </summary>
    public async Task<NextResult> PeekAsync(int position, CancellationToken ct)
    {
        var o = Settings.Swipe;
        var now = time.GetUtcNow();
        var latest = await store.LatestAsync(ct);
        var startedToday = await store.StartedSinceAsync(DayStart(now), ct);
        var decision = SessionPolicy.Decide(o, now, Settings.Zone, latest, startedToday);
        if (decision.Gate == Gate.Expire)
        {
            latest = latest! with { EndedAt = SessionPolicy.EndOfExpired(latest).UtcDateTime, EndReason = "time" };
            decision = SessionPolicy.Decide(o, now, Settings.Zone, latest, startedToday);
        }

        return decision.Gate switch
        {
            Gate.Summary => await SummaryResultAsync(latest!, ct),
            Gate.Closed => new NextResult("closed", Closed: new ClosedView(decision.Until, decision.Why ?? "cooldown")),
            Gate.Start => new NextResult("closed", Closed: new ClosedView(null, "not-started")),
            _ => await CardAsync(latest!, position, now, false, ct),
        };
    }

    /// <summary>With <paramref name="markShown"/> the card counts as shown and a position past the end ends the session; without it the session is left as it is.</summary>
    private async Task<NextResult> CardAsync(SessionRow s, int position, DateTimeOffset now, bool markShown, CancellationToken ct)
    {
        if (position < 0)
        {
            position = 0;
        }

        if (position >= s.CardCap || position >= s.QueueItems.Length)
        {
            var reason = s.QueueItems.Length >= s.CardCap ? "cards" : "exhausted";
            if (!markShown)
            {
                return await SummaryResultAsync(s.EndedAt is null ? s with { EndReason = reason } : s, ct);
            }

            await store.EndAsync(s.Id, reason, now, ct);
            return await SummaryResultAsync((await store.GetAsync(s.Id, ct))!, ct);
        }

        var kind = s.QueueKinds.ElementAtOrDefault(position) ?? "item";
        if (kind is not ("item" or "digest"))
        {
            return await SpecialCardAsync(s, position, kind, now, markShown, ct);
        }

        var item = await items.GetAsync(s.QueueItems[position], ct);
        if (item is null)
        {
            // The item was deleted since the queue was built: say nothing, move on.
            await RaiseShownAsync(s, position, markShown, ct);
            return await CardAsync(s, position + 1, now, markShown, ct);
        }

        await RaiseShownAsync(s, position, markShown, ct);
        var category = s.QueueCategories.ElementAtOrDefault(position);
        return new NextResult("card", Card: new CardView(
            s.Id, position, Math.Min(s.CardCap, s.QueueItems.Length), item.Title, item.Feed, string.IsNullOrEmpty(category) ? null : category,
            Why(item), Html.SafeUrl(item.Url), Age(item.PublishedAt, now), item.Suggestion is not null, kind,
            Detail: kind == "digest" ? new Dictionary<string, string?> { ["summary"] = item.Summary, ["suggestion"] = item.Suggestion } : null)
        { Acts = ActsFor(kind) });
    }

    /// <summary>A recall question, a post seed or an inbox entry. One that no longer exists is skipped like a deleted item.</summary>
    private async Task<NextResult> SpecialCardAsync(SessionRow s, int position, string kind, DateTimeOffset now, bool markShown, CancellationToken ct)
    {
        var category = s.QueueCategories.ElementAtOrDefault(position);
        var total = Math.Min(s.CardCap, s.QueueItems.Length);
        CardView? view = null;
        if (kind == "recall" && await recall.CardAsync(s.QueueItems[position], ct) is { } card)
        {
            view = new CardView(s.Id, position, total, card.Front, "recall", Blank(category), "", null, "", false, "recall", card.Back);
        }
        else if (kind == "seed" && await recall.SeedAsync(s.QueueItems[position], ct) is { } seed && await items.GetAsync(seed.ItemId, ct) is { } source)
        {
            view = new CardView(s.Id, position, total, seed.Angle, "post seed", Blank(category), "From: " + source.Title, Html.SafeUrl(source.Url), Age(source.PublishedAt, now), false, "seed");
        }
        else if (IsInbox(kind) && await inbox.GetAsync(s.QueueItems[position], ct) is { } row)
        {
            view = await inboxCards.ViewAsync(row, s, position, total, now, markShown, ct);
        }

        await RaiseShownAsync(s, position, markShown, ct);
        return view is null ? await CardAsync(s, position + 1, now, markShown, ct) : new NextResult("card", Card: view with { Acts = ActsFor(kind) });
    }

    private Task RaiseShownAsync(SessionRow s, int position, bool markShown, CancellationToken ct) =>
        markShown ? store.RaiseShownAsync(s.Id, position + 1, ct) : Task.CompletedTask;

    private static string? Blank(string? category) => string.IsNullOrEmpty(category) ? null : category;

    private async Task<SessionRow?> StartAsync(DateTimeOffset now, CancellationToken ct)
    {
        var queue = await BuildQueueAsync(now, ct);
        if (queue.Count == 0)
        {
            return null;
        }

        var o = Settings.Swipe;
        var id = await store.StartAsync(o.Cards, (int)o.Cap.TotalSeconds, queue.Select(q => q.Id).ToArray(), queue.Select(q => q.Category ?? "").ToArray(),
            queue.Select(q => q.Source).ToArray(), queue.Select(q => q.Predicted is { } p ? (float)p : float.NaN).ToArray(), queue.Select(q => q.Kind).ToArray(), now, ct);
        logger.LogInformation("Swipe session {Session} started with {Count} cards", id, queue.Count);
        return await store.GetAsync(id, ct);
    }

    /// <summary>
    /// Notices, the latest digest's unhandled items and open inbox entries come first, in that order and unshuffled; then due recall cards and seeds
    /// (up to their limits) and ranked items fill the rest, shuffled together.
    /// </summary>
    internal async Task<IReadOnlyList<QueuedCard>> BuildQueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var o = Settings.Swipe;
        var entries = await inbox.EligibleAsync(now, o.Cards, ct);
        var digest = await store.DigestSlotsAsync(now.AddDays(-3), ct);
        var head = entries.Where(e => e.Kind == InboxKinds.Notice).Select(e => new QueuedCard(e.Id, null, "notice", null, "notice"))
            .Concat(digest.Select(id => new QueuedCard(id, null, "digest", null, "digest")))
            .Concat(entries.Where(e => e.Kind != InboxKinds.Notice).Select(e => new QueuedCard(e.Id, null, "inbox", null, e.Kind)))
            .Take(o.Cards).ToList();

        var special = new List<QueuedCard>();
        var room = o.Cards - head.Count;
        if (o.Recall.Enabled && room > 0)
        {
            special.AddRange((await recall.DueAsync(now, Math.Min(o.Recall.PerSession, room), ct)).Select(c => new QueuedCard(c.Id, c.Category, "due", null, "recall")));
        }

        if (o.Seeds.Enabled && room - special.Count > 0)
        {
            special.AddRange((await recall.NewSeedsAsync(Math.Min(o.Seeds.PerSession, room - special.Count), ct)).Select(x => new QueuedCard(x.Id, x.Category, "seed", null, "seed")));
        }

        var rest = await BuildItemQueueAsync(Math.Max(0, room - special.Count), digest, now, ct);
        var random = new Random();
        return [.. head, .. rest.Concat(special).OrderBy(_ => random.Next())];
    }

    private async Task<IReadOnlyList<QueuedCard>> BuildItemQueueAsync(int slots, IReadOnlyCollection<long> exclude, DateTimeOffset now, CancellationToken ct)
    {
        var o = Settings;
        var candidates = await store.CandidatesAsync(now.AddDays(-o.Swipe.CandidateDays), now.AddDays(-o.Swipe.RepeatDays), exclude, ct);
        if (candidates.Count == 0 || slots == 0)
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
                c.Id, predicted is { } p ? score + (o.Swipe.Yield * (p - 0.5)) : score, Categorizer.Nearest(c.Embedding, cats, o.Swipe.CategoryFloor), c.Embedding, predicted);
        }).ToList();
        var shares = cats.Where(c => c.Share is > 0).ToDictionary(c => c.Key, c => (double)c.Share!.Value, StringComparer.Ordinal);
        return SwipeQueue.Build(scored, shares, slots, Random.Shared.Next(), o.Swipe.Epsilon);
    }

    /// <summary>
    /// With a <paramref name="clientId"/> the act is claimed first: a repeat runs nothing and returns the first call's answer, also while that call is
    /// still running. Without one it runs every time and cannot be undone.
    /// </summary>
    public async Task<ActResult> ActAsync(long sessionId, int position, string act, Guid? clientId, CancellationToken ct)
    {
        var queued = await QueuedAsync(sessionId, position, ct);
        if (queued is null || !ActsFor(queued.Kind).Contains(act, StringComparer.Ordinal))
        {
            return new ActResult(false, "Nothing to do.");
        }

        if (clientId is not { } id)
        {
            return await RunActAsync(queued, sessionId, position, act, ct);
        }

        (short Value, float Weight)? previous = null;
        var previouslySaved = false;
        if (act is "like" or "dislike" or "save" && await VoteItemAsync(queued, ct) is { } item)
        {
            previous = await feedback.VoteOfAsync(item, ct);
            previouslySaved = act == "save" && await libraryStore.IsSavedAsync(item, ct);
        }

        string? previousSource = null;
        if (act == "remove" && queued.Kind == InboxKinds.Resurface && await inbox.GetAsync(queued.RefId, ct) is { ItemId: { } removing })
        {
            previousSource = (await libraryStore.GetAsync(removing, ct))?.Source;
        }

        if (await acts.ClaimAsync(id, sessionId, position, act, previous, previouslySaved, ct, previousSource) is { } stored)
        {
            return stored.SessionId != sessionId || stored.Position != position
                ? new ActResult(false, "Nothing to do.")
                : new ActResult(stored.Ok ?? true, stored.Message ?? "Already done");
        }

        ActResult result;
        try
        {
            result = await RunActAsync(queued, sessionId, position, act, ct);
        }
        catch
        {
            await acts.CompleteAsync(id, false, "Could not do that.", CancellationToken.None);
            throw;
        }

        await acts.CompleteAsync(id, result.Ok, result.Message, CancellationToken.None);
        return result;
    }

    private async Task<ActResult> RunActAsync(Queued queued, long sessionId, int position, string act, CancellationToken ct)
    {
        if (IsInbox(queued.Kind))
        {
            return await InboxActAsync(queued, sessionId, position, act, ct);
        }

        var message = queued.Kind switch
        {
            "recall" => await RecallActAsync(queued.RefId, sessionId, act, ct),
            "seed" => await SeedActAsync(queued.RefId, act, ct),
            _ => await ItemActAsync(queued.RefId, act, ct),
        };
        if (message is null)
        {
            return new ActResult(false, "Could not do that.");
        }

        await RecordActAsync(sessionId, position, queued, act, ct);
        return new ActResult(true, message);
    }

    /// <summary>The item a like, dislike or save on this card votes on: the item itself, or a follow card's item.</summary>
    private async Task<long?> VoteItemAsync(Queued queued, CancellationToken ct) => queued.Kind switch
    {
        "item" or "digest" => queued.RefId,
        InboxKinds.Follow => (await inbox.GetAsync(queued.RefId, ct))?.ItemId,
        _ => null,
    };

    /// <summary>
    /// Takes back an act the client sent with <paramref name="clientId"/>, within 24 hours (ruling R-c): votes are restored to what they were, a stopped
    /// follow, a done entry and a dropped idea are reopened. What reached Plane, a duel, a recall review or a post seed cannot be taken back.
    /// </summary>
    public async Task<ActResult> UndoAsync(long sessionId, int position, Guid clientId, CancellationToken ct)
    {
        var record = await acts.GetAsync(clientId, ct);
        if (record is null || record.SessionId != sessionId || record.Position != position || await QueuedAsync(sessionId, position, ct) is not { } queued)
        {
            return new ActResult(false, "Nothing to do.");
        }

        if (record.Ok != true || record.UndoneAt is not null)
        {
            return new ActResult(false, "Can't undo that: " + (record.UndoneAt is null ? "it did not go through." : "it is already undone."));
        }

        if (time.GetUtcNow() - SessionPolicy.Utc(record.At) > UndoWindow)
        {
            return new ActResult(false, "Can't undo that: it was more than a day ago.");
        }

        var row = IsInbox(queued.Kind) ? await inbox.GetAsync(queued.RefId, ct) : null;
        if (record is { Act: "stop", Message: InboxCards.AlreadyStopped })
        {
            return new ActResult(false, "Can't undo that: the follow was already stopped.");
        }

        if (NotUndoable(queued.Kind, record.Act) is { } reason)
        {
            return new ActResult(false, "Can't undo that: " + reason);
        }

        if (!await acts.MarkUndoneAsync(clientId, time.GetUtcNow(), ct))
        {
            return new ActResult(false, "Can't undo that: it is already undone.");
        }

        var undoneMessage = "Undone";
        if (record.Act is "like" or "dislike" or "save" && await VoteItemAsync(queued, ct) is { } item)
        {
            if (record.PreviousValue is { } value)
            {
                await feedback.SetVoteAsync(item, value, ct, record.PreviousWeight ?? 1);
            }
            else
            {
                await feedback.ClearVoteAsync(item, ct);
            }

            if (record is { Act: "save" })
            {
                var removed = record.PreviouslySaved == false && await library.UnsaveAsync(item, ct);
                undoneMessage = removed ? "Undone; removed from your library" : "Undone; it stays in your library";
            }
        }

        if (row is not null && record.Act != "open")
        {
            if (record is { Act: "stop" } && row.RefId is { } followId)
            {
                await follows.ReopenAsync(followId, ct);
            }

            if (record is { Act: "remove", PreviousSource: { } source } && row.ItemId is { } removedItem)
            {
                var savedAt = row.Payload["savedAt"] is JsonValue s && s.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var parsed) ? parsed : time.GetUtcNow();
                await libraryStore.AddAsync(removedItem, source, savedAt, ct);
                undoneMessage = "Undone; back in your library";
            }

            if (record is { Act: "read", Message: not InboxCards.AlreadyRead } && row.ItemId is { } readItem)
            {
                await libraryStore.MarkReadAsync(readItem, false, time.GetUtcNow(), ct);
            }

            if (record is { Act: "drop" } && row.Payload["url"] is JsonValue { } url && url.TryGetValue<string>(out var canonical))
            {
                await ideas.UndropAsync(canonical, ct);
            }

            await inbox.ReopenAsync(row.Id, ct);
        }

        if (await store.EventAsync(sessionId, position, ct) is { } existing)
        {
            var remaining = existing.Acts.Where(a => a != record.Act).ToArray();
            await store.SaveEventAsync(existing with { Acts = remaining, Action = Primary(remaining, existing.Direction) }, ct);
        }

        return new ActResult(true, undoneMessage);
    }

    private static string? NotUndoable(string kind, string act) => (kind, act) switch
    {
        ("recall", _) => "the card was already reviewed.",
        ("seed", _) => "the post seed was already handled.",
        (_, "idea") or (InboxKinds.Idea, "keep") => "it was filed in Plane.",
        (_, "pick_a" or "pick_b") => "the duel was answered.",
        _ => null,
    };

    private async Task<string?> ItemActAsync(long itemId, string act, CancellationToken ct) => act switch
    {
        "like" => await VoteAsync(itemId, 1, SwipeVoteWeight, "Liked", ct),
        "dislike" => await VoteAsync(itemId, -1, SwipeVoteWeight, "Not for me", ct),
        "save" => await SaveAsync(itemId, ct),
        "idea" => await IdeaAsync(itemId, ct),
        _ => "",
    };

    /// <summary>An inbox card. Whatever it did, the entry is done afterwards, except for <c>open</c> and for ideas (<see cref="IdeaDecisions"/> sets that itself).</summary>
    private async Task<ActResult> InboxActAsync(Queued queued, long sessionId, int position, string act, CancellationToken ct)
    {
        if (await inbox.GetAsync(queued.RefId, ct) is not { } row)
        {
            return new ActResult(false, "Could not do that.");
        }

        ActResult result;
        if (row.Kind == InboxKinds.Follow && act is "save" or "like" or "dislike")
        {
            var message = row.ItemId is { } itemId ? await ItemActAsync(itemId, act, ct) : null;
            result = message is null ? new ActResult(false, "Could not do that.") : new ActResult(true, message);
        }
        else
        {
            result = await inboxCards.ActAsync(row, act, ct);
        }

        if (!result.Ok)
        {
            return result;
        }

        if (act != "open" && row.Kind is InboxKinds.Follow or InboxKinds.Duel or InboxKinds.Resurface)
        {
            await inboxCards.DoneAsync(row.Id, ct);
        }

        await RecordActAsync(sessionId, position, queued, act, ct);
        return result;
    }

    private async Task RecordActAsync(long sessionId, int position, Queued queued, string act, CancellationToken ct)
    {
        var existing = await store.EventAsync(sessionId, position, ct) ?? NewEvent(sessionId, position, queued);
        var acts = existing.Acts.Append(act).Distinct(StringComparer.Ordinal).ToArray();
        await store.SaveEventAsync(existing with { Acts = acts, Action = Primary(acts, existing.Direction) }, ct);
    }

    /// <summary>Every queue kind but these names an inbox entry.</summary>
    private static bool IsInbox(string kind) => kind is not ("item" or "digest" or "recall" or "seed");

    private static string[] ActsFor(string kind) => kind switch
    {
        "item" or "digest" => Acts,
        "recall" => RecallActs,
        "seed" => SeedActs,
        _ => InboxCards.ActsFor(kind),
    };

    private static EventRow NewEvent(long sessionId, int position, Queued q) =>
        new() { SessionId = sessionId, Position = position, Kind = q.Kind, RefId = q.RefId, Category = q.Category, RankSource = q.Source, Predicted = q.Predicted };

    private async Task<string?> RecallActAsync(long cardId, long sessionId, string act, CancellationToken ct)
    {
        if (await recall.CardAsync(cardId, ct) is not { } card)
        {
            return null;
        }

        await recall.ReviewAsync(card, sessionId, act == "good", time.GetUtcNow(), ct);
        return act == "good" ? "Good" : "Again tomorrow";
    }

    private async Task<string?> SeedActAsync(long seedId, string act, CancellationToken ct)
    {
        if (await recall.SeedAsync(seedId, ct) is not { Status: "new" } seed)
        {
            return await recall.SeedAsync(seedId, ct) is null ? null : "Already handled";
        }

        var now = time.GetUtcNow();
        if (act == "dismiss")
        {
            await recall.ResolveSeedAsync(seed.Id, "dismissed", null, now, ct);
            return "Dismissed";
        }

        if (await feedback.GetIdeaAsync(seed.ItemId, ct) is { } filed)
        {
            await recall.ResolveSeedAsync(seed.Id, "sent", filed.PlaneProject, now, ct);
            return $"That item already has an idea in {filed.PlaneProject}";
        }

        var (project, error) = await callbacks.FileIdeaAsync(seed.ItemId, null, "[post seed] " + seed.Angle, ct);
        if (project is null)
        {
            logger.LogWarning("Sending post seed {Seed} failed: {Error}", seed.Id, error);
            return null;
        }

        await recall.ResolveSeedAsync(seed.Id, "sent", project, now, ct);
        return $"Sent to {project}";
    }

    /// <summary>What happened to a card when it was left (or the page was hidden): the direction, the dwell and the time it was shown. Idempotent; the last call wins.</summary>
    public async Task<bool> EventAsync(long sessionId, int position, long? shownAtMs, long? dwellMs, string? direction, CancellationToken ct)
    {
        var session = await store.GetAsync(sessionId, ct);
        if (QueuedIn(session, position, true) is not { } queued)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var shown = shownAtMs is > 0 && DateTimeOffset.FromUnixTimeMilliseconds(shownAtMs.Value) is var at && Math.Abs((at - now).TotalDays) < 1 ? at : (DateTimeOffset?)null;
        if (position >= session!.CardsShown)
        {
            // A card fetched ahead with a peek counts as shown from its first event. Once the session is over (ended or past its time cap) only
            // an event saying the card was shown while it ran counts: the phone may send what it swiped offline later, within the act window.
            var started = SessionPolicy.Utc(session.StartedAt);
            var over = started + TimeSpan.FromSeconds(session.TimeCapS);
            if (session.EndedAt is { } ended && SessionPolicy.Utc(ended) < over)
            {
                over = SessionPolicy.Utc(ended);
            }

            if (now >= over && (shown is not { } seen || seen < started || seen > over))
            {
                return false;
            }

            await store.RaiseShownAsync(session.Id, position + 1, ct);
        }

        // Events can arrive out of order after a peek, so an inbox showing is counted on every event; it counts once per session.
        if (IsInbox(queued.Kind) && await inbox.GetAsync(queued.RefId, ct) is { } entry)
        {
            await inboxCards.ViewAsync(entry, session, position, Math.Min(session.CardCap, session.QueueItems.Length), now, true, ct);
        }

        var existing = await store.EventAsync(sessionId, position, ct) ?? NewEvent(sessionId, position, queued);
        var dir = direction is not null && Directions.Contains(direction, StringComparer.Ordinal) ? direction : "none";
        await store.SaveEventAsync(existing with
        {
            Direction = dir,
            ShownAt = shown?.UtcDateTime,
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

    private async Task<Queued?> QueuedAsync(long sessionId, int position, CancellationToken ct) => QueuedIn(await store.GetAsync(sessionId, ct), position, false);

    /// <summary>The queue entry at a shown position, or with <paramref name="peeked"/> at any position inside the queue and the cap.</summary>
    private Queued? QueuedIn(SessionRow? s, int position, bool peeked)
    {
        var limit = peeked ? Math.Min(s?.CardCap ?? 0, s?.QueueItems.Length ?? 0) : s?.CardsShown ?? 0;
        if (s is null || (s.EndedAt is { } ended && time.GetUtcNow() - SessionPolicy.Utc(ended) > UndoWindow) || position < 0 || position >= limit || position >= s.QueueItems.Length)
        {
            return null;
        }

        var category = s.QueueCategories.ElementAtOrDefault(position);
        var source = s.QueueSources.ElementAtOrDefault(position) ?? SwipeQueue.Exploit;
        var predicted = s.QueuePredicted.Length > position && !float.IsNaN(s.QueuePredicted[position]) ? s.QueuePredicted[position] : (float?)null;
        var kind = s.QueueKinds.ElementAtOrDefault(position) ?? "item";
        return new Queued(kind, s.QueueItems[position], string.IsNullOrEmpty(category) ? null : category, source, predicted);
    }

    private async Task<string?> VoteAsync(long itemId, short value, double weight, string message, CancellationToken ct)
    {
        await feedback.SetVoteAsync(itemId, value, ct, weight);
        return message;
    }

    /// <summary>A save goes into the library, which also records the firm yes (weight 1).</summary>
    private async Task<string> SaveAsync(long itemId, CancellationToken ct) =>
        (await library.SaveAsync(itemId, "swipe", ct)).Status == SaveStatus.AlreadySaved ? "Already saved" : "Saved";

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
        Strongest.FirstOrDefault(acts.Contains) ?? (direction == "down" ? "back" : "next");

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

    internal static string Age(DateTime published, DateTimeOffset now)
    {
        var age = now - new DateTimeOffset(DateTime.SpecifyKind(published, DateTimeKind.Utc));
        return age.TotalHours < 1 ? "just now" : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} d ago";
    }
}
