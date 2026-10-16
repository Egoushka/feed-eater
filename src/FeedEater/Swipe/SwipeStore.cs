using Dapper;
using FeedEater.Storage;

namespace FeedEater.Swipe;

public sealed record SwipeCandidate
{
    public long Id { get; init; }
    public long? FeedId { get; init; }
    public float[] Embedding { get; init; } = [];
}

public sealed record EventRow
{
    public long SessionId { get; init; }
    public int Position { get; init; }
    public long RefId { get; init; }
    public string? Category { get; init; }
    public string RankSource { get; init; } = "exploit";
    public float? Predicted { get; init; }
    public DateTime? ShownAt { get; init; }
    public int? DwellMs { get; init; }
    public string? Direction { get; init; }
    public string? Action { get; init; }
    public string[] Acts { get; init; } = [];
}

public sealed record SessionSummaryData(int CardsShown, double Minutes, IReadOnlyDictionary<string, int> Acts, IReadOnlyList<(string Category, int Cards)> Categories);

public sealed record WeekRow
{
    public DateTime Week { get; init; }
    public int Sessions { get; init; }
    public int Cards { get; init; }
    public double Minutes { get; init; }
    public int Likes { get; init; }
    public int Dislikes { get; init; }
    public int Saves { get; init; }
    public int Shares { get; init; }
    public int Ideas { get; init; }
    public int Opens { get; init; }
    public double? AvgDwellS { get; init; }
    public int Lighter { get; init; }
    public int Same { get; init; }
    public int Drained { get; init; }
}

public sealed record CategoryCount(string Category, int Cards);

/// <summary>Cards shown by how they were chosen, with how many drew a good act (idea, save or share) and what the model expected of them.</summary>
public sealed record SourceYield
{
    public string Source { get; init; } = "";
    public int Cards { get; init; }
    public int Good { get; init; }
    public double? AvgPredicted { get; init; }
}

public sealed class SwipeStore(FeedDb db)
{
    private const string SessionColumns =
        "id, started_at, ended_at, end_reason, card_cap, time_cap_s, cards_shown, queue_items, queue_categories, queue_sources, queue_predicted, felt, felt_at";

    /// <summary>
    /// Items published after <paramref name="publishedAfter"/> that are embedded, not duplicates, not in a muted feed, not voted on and not shown
    /// in swipe mode since <paramref name="shownSince"/>; one per story (the earliest of its cluster).
    /// </summary>
    public async Task<IReadOnlyList<SwipeCandidate>> CandidatesAsync(DateTimeOffset publishedAfter, DateTimeOffset shownSince, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<SwipeCandidate>(new CommandDefinition(
            """
            select distinct on (coalesce(i.cluster_of, i.id)) i.id, i.feed_id, i.embedding::real[] as embedding
            from items i left join feeds f on f.id = i.feed_id
            where i.published_at > @publishedAfter and i.duplicate_of is null and i.embedding is not null
              and not coalesce(f.muted, false)
              and not exists (select 1 from votes v where v.item_id = i.id)
              and not exists (select 1 from swipe_events e where e.ref_id = i.id and e.created_at > @shownSince)
            order by coalesce(i.cluster_of, i.id), i.published_at, i.id
            """,
            new { publishedAfter = publishedAfter.UtcDateTime, shownSince = shownSince.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    public async Task<SessionRow?> LatestAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            $"select {SessionColumns} from swipe_sessions order by id desc limit 1", cancellationToken: ct));
    }

    public async Task<SessionRow?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            $"select {SessionColumns} from swipe_sessions where id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<int> StartedSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from swipe_sessions where started_at >= @since", new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<long> StartAsync(int cardCap, int timeCapS, long[] items, string[] categories, string[] sources, float[] predicted, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            insert into swipe_sessions (started_at, card_cap, time_cap_s, queue_items, queue_categories, queue_sources, queue_predicted)
            values (@at, @cardCap, @timeCapS, @items, @categories, @sources, @predicted) returning id
            """, new { at = at.UtcDateTime, cardCap, timeCapS, items, categories, sources, predicted }, cancellationToken: ct));
    }

    public async Task EndAsync(long id, string reason, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update swipe_sessions set ended_at = @at, end_reason = @reason where id = @id and ended_at is null",
            new { id, reason, at = at.UtcDateTime }, cancellationToken: ct));
    }

    public async Task RaiseShownAsync(long id, int count, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update swipe_sessions set cards_shown = greatest(cards_shown, @count) where id = @id", new { id, count }, cancellationToken: ct));
    }

    /// <summary>Records how the session left the owner; only a session that has ended can be rated, and the first answer stays.</summary>
    public async Task<bool> SetFeltAsync(long id, short felt, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "update swipe_sessions set felt = @felt, felt_at = @at where id = @id and ended_at is not null and felt_at is null",
            new { id, felt, at = at.UtcDateTime }, cancellationToken: ct)) == 1;
    }

    public async Task<EventRow?> EventAsync(long sessionId, int position, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<EventRow>(new CommandDefinition(
            """
            select session_id, position, ref_id, category, rank_source, predicted, shown_at, dwell_ms, direction, action, acts
            from swipe_events where session_id = @sessionId and position = @position
            """, new { sessionId, position }, cancellationToken: ct));
    }

    public async Task SaveEventAsync(EventRow e, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into swipe_events (session_id, position, kind, ref_id, category, rank_source, predicted, shown_at, dwell_ms, direction, action, acts)
            values (@SessionId, @Position, 'item', @RefId, @Category, @RankSource, @Predicted, @ShownAt, @DwellMs, @Direction, @Action, @Acts)
            on conflict (session_id, position) do update set
                shown_at = coalesce(excluded.shown_at, swipe_events.shown_at),
                dwell_ms = coalesce(excluded.dwell_ms, swipe_events.dwell_ms),
                direction = coalesce(excluded.direction, swipe_events.direction),
                action = excluded.action,
                acts = excluded.acts
            """, e, cancellationToken: ct));
    }

    public async Task<SessionSummaryData> SummaryAsync(SessionRow s, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var acts = (await c.QueryAsync<(string Act, int N)>(new CommandDefinition(
            "select a as act, count(*)::int as n from swipe_events, unnest(acts) a where session_id = @id group by a", new { id = s.Id }, cancellationToken: ct)))
            .ToDictionary(r => r.Act, r => r.N);
        var categories = (await c.QueryAsync<(string Category, int N)>(new CommandDefinition(
            """
            select category, count(*)::int as n from swipe_events
            where session_id = @id and category is not null and category <> '' group by category order by n desc, category limit 3
            """, new { id = s.Id }, cancellationToken: ct))).Select(r => (r.Category, r.N)).ToList();
        var end = s.EndedAt is { } e ? SessionPolicy.Utc(e) : now;
        return new SessionSummaryData(s.CardsShown, Math.Max(0, (end - SessionPolicy.Utc(s.StartedAt)).TotalMinutes), acts, categories);
    }

    public async Task<IReadOnlyList<WeekRow>> WeeksAsync(string timeZone, int weeks, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<WeekRow>(new CommandDefinition(
            """
            with s as (
                select id, date_trunc('week', started_at at time zone @tz) as week, cards_shown, felt,
                       extract(epoch from (coalesce(ended_at, started_at) - started_at)) / 60.0 as minutes
                from swipe_sessions where started_at > now() - make_interval(weeks => @weeks)),
            e as (
                select s.week,
                       count(*) filter (where 'like' = any(ev.acts))::int as likes,
                       count(*) filter (where 'dislike' = any(ev.acts))::int as dislikes,
                       count(*) filter (where 'save' = any(ev.acts))::int as saves,
                       count(*) filter (where 'share' = any(ev.acts))::int as shares,
                       count(*) filter (where 'idea' = any(ev.acts))::int as ideas,
                       count(*) filter (where 'open' = any(ev.acts))::int as opens,
                       avg(ev.dwell_ms) / 1000.0 as avg_dwell_s
                from s join swipe_events ev on ev.session_id = s.id group by s.week)
            select s.week, count(*)::int as sessions, coalesce(sum(s.cards_shown), 0)::int as cards, coalesce(sum(s.minutes), 0)::float8 as minutes,
                   coalesce(max(e.likes), 0) as likes, coalesce(max(e.dislikes), 0) as dislikes, coalesce(max(e.saves), 0) as saves,
                   coalesce(max(e.shares), 0) as shares, coalesce(max(e.ideas), 0) as ideas, coalesce(max(e.opens), 0) as opens,
                   max(e.avg_dwell_s)::float8 as avg_dwell_s,
                   count(*) filter (where s.felt = 1)::int as lighter, count(*) filter (where s.felt = 0)::int as same,
                   count(*) filter (where s.felt = -1)::int as drained
            from s left join e on e.week = s.week group by s.week order by s.week desc
            """, new { tz = timeZone, weeks }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<SourceYield>> SourceYieldAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<SourceYield>(new CommandDefinition(
            """
            select rank_source as source, count(*)::int as cards,
                   count(*) filter (where acts && array['idea', 'save', 'share'])::int as good,
                   avg(nullif(predicted, 'NaN'::real))::float8 as avg_predicted
            from swipe_events where created_at > @since group by rank_source order by rank_source
            """, new { since = since.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<CategoryCount>> CategoryCardsAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<CategoryCount>(new CommandDefinition(
            """
            select coalesce(nullif(category, ''), '(none)') as category, count(*)::int as cards
            from swipe_events where created_at > @since group by 1 order by cards desc
            """, new { since = since.UtcDateTime }, cancellationToken: ct))).ToList();
    }
}

public sealed record CategoryRow
{
    public string Key { get; init; } = "";
    public string Description { get; init; } = "";
    public float? Share { get; init; }
    public string[] Events { get; init; } = [];
    public bool Seed { get; init; }
    public float[] Embedding { get; init; } = [];
}

public sealed class CategoryStore(FeedDb db)
{
    /// <summary>Replaces the whole set, so categories removed from the file stop counting.</summary>
    public async Task ReplaceAllAsync(IReadOnlyList<CategoryRow> rows, DateTimeOffset builtAt, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("delete from categories", transaction: tx, cancellationToken: ct));
        foreach (var r in rows)
        {
            await c.ExecuteAsync(new CommandDefinition(
                """
                insert into categories (key, description, share, events, seed, embedding, built_at)
                values (@Key, @Description, @Share, @Events, @Seed, @Embedding::real[]::vector, @builtAt)
                """, new { r.Key, r.Description, r.Share, r.Events, r.Seed, r.Embedding, builtAt = builtAt.UtcDateTime }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CategoryRow>> AllAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<CategoryRow>(new CommandDefinition(
            "select key, description, share, events, seed, embedding::real[] as embedding from categories order by key", cancellationToken: ct))).ToList();
    }
}
