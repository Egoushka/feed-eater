using Dapper;
using FeedEater.Storage;

namespace FeedEater.Swipe;

public sealed record IdeaItem
{
    public long Id { get; init; }
    public string SeedKey { get; init; } = "";
    public string Body { get; init; } = "";
    public string SourceSession { get; init; } = "";
    public DateTime SourceDate { get; init; }
    public string? Repo { get; init; }
    public string Status { get; init; } = "active";
    public double Stability { get; init; }
    public double Difficulty { get; init; }
    public int Reps { get; init; }
    public DateTime DueAt { get; init; }
    public DateTime? LastReviewAt { get; init; }
}

public sealed record IdeaSeed(string Key, string Body, string Session, DateOnly Date, string? Repo);

/// <summary>Ideas that existed only in a transcript, and when each should come back.</summary>
public sealed class IdeaStore(FeedDb db)
{
    private const string Columns = "id, seed_key as SeedKey, body, source_session as SourceSession, source_date::timestamp as SourceDate, repo, status, stability, difficulty, reps, due_at as DueAt, last_review_at as LastReviewAt";

    /// <summary>Inserts the seeds that are not there yet, matched by key. Returns how many were added.</summary>
    public async Task<int> SeedAsync(IEnumerable<IdeaSeed> seeds, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var added = 0;
        foreach (var s in seeds)
        {
            added += await c.ExecuteAsync(new CommandDefinition(
                "insert into idea_items (seed_key, body, source_session, source_date, repo) values (@Key, @Body, @Session, @Date, @Repo) on conflict (seed_key) do nothing",
                new { s.Key, s.Body, s.Session, Date = s.Date.ToDateTime(TimeOnly.MinValue), s.Repo }, cancellationToken: ct));
        }

        return added;
    }

    public async Task<IdeaItem?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<IdeaItem>(new CommandDefinition($"select {Columns} from idea_items where id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Active ideas that are due, never reviewed ones first, then the lowest retrievability; the caller applies the caps.</summary>
    public async Task<IReadOnlyList<IdeaItem>> DueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<IdeaItem>(new CommandDefinition(
            $"select {Columns} from idea_items where status = 'active' and due_at <= @now order by id", new { now = now.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    /// <summary>How many ideas went into a stack since <paramref name="since"/>.</summary>
    public async Task<int> QueuedSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition("select count(*) from idea_items where queued_at >= @since", new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    public async Task MarkQueuedAsync(IReadOnlyCollection<long> ids, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update idea_items set queued_at = @now where id = any(@ids)", new { now = now.UtcDateTime, ids = ids.ToArray() }, cancellationToken: ct));
    }

    public async Task ReviewAsync(IdeaItem idea, int grade, DateTimeOffset now, CancellationToken ct)
    {
        var elapsed = idea.LastReviewAt is { } last ? (now - new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc))).TotalDays : 0;
        var state = Fsrs.Review(idea.Reps == 0 ? null : new FsrsState(idea.Stability, idea.Difficulty), elapsed, grade);
        var due = now.AddDays(Fsrs.Interval(state.Stability));
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update idea_items set stability = @s, difficulty = @d, reps = reps + 1, lapses = lapses + @lapse, due_at = @due, last_review_at = @now where id = @id and status = 'active'",
            new { s = state.Stability, d = state.Difficulty, lapse = grade == Fsrs.Again ? 1 : 0, due = due.UtcDateTime, now = now.UtcDateTime, id = idea.Id }, cancellationToken: ct));
    }

    public async Task SetStatusAsync(long id, string status, int? issueNumber, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update idea_items set status = @status, issue_number = coalesce(@issueNumber, issue_number) where id = @id", new { id, status, issueNumber }, cancellationToken: ct));
    }
}

/// <summary>Which due ideas go into a stack. Pure.</summary>
public static class IdeaPicker
{
    /// <summary>Lowest retrievability first (a never reviewed idea counts as 0), at most <paramref name="slots"/>, each idea once.</summary>
    public static IReadOnlyList<IdeaItem> Pick(IEnumerable<IdeaItem> due, DateTimeOffset now, int slots) =>
        slots <= 0 ? [] : due.Where(i => i.Status == "active").DistinctBy(i => i.Id)
            .OrderBy(i => Retrievability(i, now)).ThenBy(i => i.Id).Take(slots).ToList();

    public static double Retrievability(IdeaItem idea, DateTimeOffset now) =>
        idea.Reps == 0 || idea.LastReviewAt is null ? 0
            : Fsrs.Retrievability((now - new DateTimeOffset(DateTime.SpecifyKind(idea.LastReviewAt.Value, DateTimeKind.Utc))).TotalDays, idea.Stability);
}

/// <summary>The 9 ideas that existed only in a transcript (docs/specs/2026-11-20-labels-and-idea-resurfacing.md), in his words.</summary>
public static class LostIdeaSeed
{
    public static readonly IReadOnlyList<IdeaSeed> All =
    [
        new("2026-11-11/4b27c0b6", "i'm thinking about new approaches, filesystems, system tools, maybe another linux distro that will give full controll over everything so we can controll every step of memory taking and cut in down as much as we can", "4b27c0b6", new(2026, 11, 11), "Egoushka/homelab"),
        new("2026-11-11/8198e678-cache", "we need to have cache and one on our side, cause agents cannot have common context and this cache should be more agent proactive, idk and should be part of collector, sink i think", "8198e678", new(2026, 11, 11), "Egoushka/chargehand"),
        new("2026-11-11/8198e678-optimize", "i wanna come down and maybe reinvent something and optimize. so basically i have next constructs: cpu, ram, memory  all of those could be optimized without quality and retrieve quality somehow", "8198e678", new(2026, 11, 11), null),
        new("2026-11-12/36d4c50f", "my initial will was to make AI basically do everything with my screen and being \"with me\"", "36d4c50f", new(2026, 11, 12), "Egoushka/wheelhouse"),
        new("2026-11-12/a6df575a", "i think we could also add some step, that haiku also can do - determine what's needed to be done and if needed - that will remove cost for session that will do nothing with a result of run", "a6df575a", new(2026, 11, 12), "Egoushka/chargehand"),
        new("2026-11-11/d99bff7c", "oura-platform - need to make it more usable to people, like, that was random and 100% people that use oura and are tech a bi...", "d99bff7c", new(2026, 11, 11), "Egoushka/oura-platform"),
        new("2026-11-02/18af8793", "i wanna have some better secret management tool ... which relates to some app, how many those, how many programs refer to it ... this program should give it's own keys and that program talk to real service", "18af8793", new(2026, 11, 2), "Egoushka/homelab"),
        new("2026-11-07/2a685036", "okay record them + cached code idea", "2a685036", new(2026, 11, 7), "Egoushka/chargehand"),
        new("2026-11-10/48a93860", "i wanna have statistics over my github over all my projects cause i need to click every repo to see visits, stars, forks and all other ego-feeding info", "48a93860", new(2026, 11, 10), "Egoushka/senses"),
    ];
}
