using Dapper;
using FeedEater.Storage;

namespace FeedEater.Swipe;

public sealed record RecallCard
{
    public long Id { get; init; }
    public string Source { get; init; } = "";
    public long? ItemId { get; init; }
    public string Front { get; init; } = "";
    public string Back { get; init; } = "";
    public string? Category { get; init; }
    public int Step { get; init; }
}

public sealed record PostSeed
{
    public long Id { get; init; }
    public long ItemId { get; init; }
    public string Angle { get; init; } = "";
    public string? Category { get; init; }
    public string Status { get; init; } = "new";
}

/// <summary>A liked or saved item with its vector, waiting to be turned into cards or a seed.</summary>
public sealed record LikedItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string? Summary { get; init; }
    public string? Why { get; init; }
    public string Content { get; init; } = "";
    public float[] Embedding { get; init; } = [];
}

public sealed record RecallStats(int Good, int Again, int Remembered, int SeedsSent, int SeedsDismissed, int Due);

public sealed class RecallStore(FeedDb db)
{
    public async Task<IReadOnlyList<RecallCard>> DueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<RecallCard>(new CommandDefinition(
            "select id, source, item_id, front, back, category, step from recall_cards where not suspended and due_at <= @now order by due_at, id limit @limit",
            new { now = now.UtcDateTime, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<RecallCard?> CardAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<RecallCard>(new CommandDefinition(
            "select id, source, item_id, front, back, category, step from recall_cards where id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Records an answer and moves the card on the ladder. Answering the same card twice in a session moves it twice, which is the owner's call.</summary>
    public async Task ReviewAsync(RecallCard card, long sessionId, bool good, DateTimeOffset now, CancellationToken ct)
    {
        var (step, due) = RecallScheduler.Next(card.Step, good, now);
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "insert into recall_reviews (card_id, session_id, at, result, step_before) values (@id, @sessionId, @at, @result, @stepBefore)",
            new { id = card.Id, sessionId, at = now.UtcDateTime, result = good ? "good" : "again", stepBefore = card.Step }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "update recall_cards set step = @step, due_at = @due, reps = reps + 1, lapses = lapses + @lapse where id = @id",
            new { id = card.Id, step, due = due.UtcDateTime, lapse = good ? 0 : 1 }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<bool> IsDoneAsync(string reference, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("select exists (select 1 from recall_done where ref = @reference)", new { reference }, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<string>> DoneAmongAsync(IReadOnlyCollection<string> references, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>(new CommandDefinition("select ref from recall_done where ref = any(@references)", new { references = references.ToArray() }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Stores the cards for one source and marks the source done, in one transaction; a source with no cards is marked too, so it is not asked again.</summary>
    public async Task AddCardsAsync(string source, string reference, long? itemId, string? category, IReadOnlyList<RecallPair> pairs, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var p in pairs)
        {
            await c.ExecuteAsync(new CommandDefinition(
                "insert into recall_cards (source, ref, item_id, front, back, category, due_at) values (@source, @reference, @itemId, @front, @back, @category, @due)",
                new { source, reference, itemId, front = p.Question, back = p.Answer, category, due = now.UtcDateTime }, tx, cancellationToken: ct));
        }

        await c.ExecuteAsync(new CommandDefinition("insert into recall_done (ref, at) values (@reference, @at) on conflict do nothing", new { reference, at = now.UtcDateTime }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<int> ItemsDoneSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from recall_done where ref like 'item:%' and at >= @since", new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    /// <summary>Items with a 👍 (any weight) that have no cards yet, newest vote first.</summary>
    public async Task<IReadOnlyList<LikedItem>> LikedWithoutCardsAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<LikedItem>(new CommandDefinition(
            """
            select i.id, i.title, r.summary, r.why, left(i.content, 20000) as content, i.embedding::real[] as embedding
            from votes v join items i on i.id = v.item_id left join reads r on r.item_id = i.id
            where v.value = 1 and i.embedding is not null and not exists (select 1 from recall_done d where d.ref = 'item:' || i.id)
            order by v.at desc limit @limit
            """, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Items liked since <paramref name="since"/> that have no seed and no filed idea.</summary>
    public async Task<IReadOnlyList<LikedItem>> LikedWithoutSeedAsync(DateTimeOffset since, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<LikedItem>(new CommandDefinition(
            """
            select i.id, i.title, r.summary, r.why, '' as content, i.embedding::real[] as embedding
            from votes v join items i on i.id = v.item_id left join reads r on r.item_id = i.id
            where v.value = 1 and v.at >= @since and i.embedding is not null
              and not exists (select 1 from post_seeds s where s.item_id = i.id) and not exists (select 1 from ideas d where d.item_id = i.id)
            order by v.at desc limit @limit
            """, new { since = since.UtcDateTime, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>An item the model found no honest angle for is stored as dismissed, so it is not asked about again.</summary>
    public async Task AddSeedAsync(long itemId, string angle, string? category, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "insert into post_seeds (item_id, angle, category, status) values (@itemId, @angle, @category, @status) on conflict (item_id) do nothing",
            new { itemId, angle, category, status = angle.Length == 0 ? "dismissed" : "new" }, cancellationToken: ct));
    }

    public async Task<int> SeedsCreatedSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from post_seeds where created_at >= @since", new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PostSeed>> NewSeedsAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PostSeed>(new CommandDefinition(
            "select id, item_id, angle, category, status from post_seeds where status = 'new' order by created_at, id limit @limit", new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<PostSeed?> SeedAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<PostSeed>(new CommandDefinition(
            "select id, item_id, angle, category, status from post_seeds where id = @id", new { id }, cancellationToken: ct));
    }

    public async Task ResolveSeedAsync(long id, string status, string? sentTo, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update post_seeds set status = @status, sent_to = @sentTo, resolved_at = @at where id = @id and status = 'new'",
            new { id, status, sentTo, at = now.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<RecallStats> StatsAsync(DateTimeOffset since, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleAsync<RecallStats>(new CommandDefinition(
            """
            select (select count(*) from recall_reviews where at >= @since and result = 'good')::int as good,
                   (select count(*) from recall_reviews where at >= @since and result = 'again')::int as again,
                   (select count(*) from recall_reviews where at >= @since and result = 'good' and step_before >= 1)::int as remembered,
                   (select count(*) from post_seeds where resolved_at >= @since and status = 'sent')::int as seeds_sent,
                   (select count(*) from post_seeds where resolved_at >= @since and status = 'dismissed')::int as seeds_dismissed,
                   (select count(*) from recall_cards where not suspended and due_at <= @now)::int as due
            """, new { since = since.UtcDateTime, now = now.UtcDateTime }, cancellationToken: ct));
    }
}
