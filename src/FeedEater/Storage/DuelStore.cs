using Dapper;
using FeedEater.Duels;

namespace FeedEater.Storage;

public enum DuelOutcome { Answered, AlreadyAnswered, NoDuel }

/// <summary>What a tap did. <c>Title</c> is the picked item's, null for a skip.</summary>
public sealed record DuelAnswer(DuelOutcome Outcome, string? Title = null);

public sealed class DuelStore(FeedDb db)
{
    private sealed record Pair(long A, long B);

    private sealed record Closed(long A, long B, long? Winner);

    /// <summary>
    /// Items a duel may offer: published since <paramref name="publishedAfter"/>, embedded, scored, triaged at relevance 1 or more,
    /// not voted, not in a muted feed, one per story (the earliest published).
    /// </summary>
    public async Task<IReadOnlyList<DuelItem>> PoolAsync(DateTimeOffset publishedAfter, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<DuelItem>(new CommandDefinition(
            """
            select distinct on (coalesce(i.cluster_of, i.id))
                   i.id, coalesce(i.cluster_of, i.id) as story_id, i.title, i.url, coalesce(f.title, '') as feed,
                   i.score::double precision as score, i.embedding::real[] as embedding
            from items i
            join triage t on t.item_id = i.id
            left join feeds f on f.id = i.feed_id
            where i.published_at > @publishedAfter and i.duplicate_of is null and i.embedding is not null and i.score is not null
              and t.relevance >= 1 and not coalesce(f.muted, false)
              and not exists (select 1 from votes v where v.item_id = i.id)
            order by coalesce(i.cluster_of, i.id), i.published_at, i.id
            """, new { publishedAfter = publishedAfter.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    /// <summary>Every pair ever sent, answered or not, so no pair is offered twice.</summary>
    public async Task<IReadOnlySet<(long, long)>> SeenPairsAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Pair>(new CommandDefinition("select a, b from duels", cancellationToken: ct));
        return rows.Select(r => DuelPicker.Pair(r.A, r.B)).ToHashSet();
    }

    public async Task<int> SentSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from duels where sent_at >= @since", new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<long> CreateAsync(long a, long b, DateTimeOffset sentAt, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "insert into duels (a, b, sent_at) values (@a, @b, @sentAt) returning id", new { a, b, sentAt = sentAt.UtcDateTime }, cancellationToken: ct));
    }

    /// <summary>For a duel whose message never went out.</summary>
    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("delete from duels where id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Records the tap once (<paramref name="pick"/> is 'a', 'b' or 's' for skip); a second tap changes nothing. The winner gets a 👍 and
    /// the loser a 👎 at half weight, each only when the item has no vote yet: a vote he cast himself is never overwritten.
    /// </summary>
    public async Task<DuelAnswer> AnswerAsync(long id, char pick, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var closed = await c.QuerySingleOrDefaultAsync<Closed>(new CommandDefinition(
            """
            update duels set winner = case @pick::text when 'a' then a when 'b' then b end, answered_at = now()
            where id = @id and answered_at is null
            returning a, b, winner
            """, new { id, pick = pick.ToString() }, tx, cancellationToken: ct));
        if (closed is not { } row)
        {
            var exists = await c.ExecuteScalarAsync<bool>(new CommandDefinition("select exists (select 1 from duels where id = @id)", new { id }, tx, cancellationToken: ct));
            return new DuelAnswer(exists ? DuelOutcome.AlreadyAnswered : DuelOutcome.NoDuel);
        }

        if (row.Winner is not { } winner)
        {
            await tx.CommitAsync(ct);
            return new DuelAnswer(DuelOutcome.Answered);
        }

        var loser = winner == row.A ? row.B : row.A;
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into votes (item_id, value, weight, at) values (@winner, 1, 1, now()), (@loser, -1, 0.5, now())
            on conflict (item_id) do nothing
            """, new { winner, loser }, tx, cancellationToken: ct));
        var title = await c.ExecuteScalarAsync<string>(new CommandDefinition("select title from items where id = @winner", new { winner }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new DuelAnswer(DuelOutcome.Answered, title);
    }
}
