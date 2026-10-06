using Dapper;

namespace FeedEater.Storage;

/// <summary>One embedded item on the taste map. <c>Vote</c> is null when the item has none; <c>Shown</c> says a sent digest carried it.</summary>
public sealed record MapItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public float[] Embedding { get; init; } = [];
    public int? Vote { get; init; }
    public double Weight { get; init; } = 1;
    public DateTime? VotedAt { get; init; }
    public bool Shown { get; init; }
}

public sealed class MapStore(FeedDb db)
{
    /// <summary>
    /// Every voted item with an embedding, plus up to <paramref name="sample"/> other embedded items published since <paramref name="since"/>.
    /// The sample is ordered by a hash of the id, so it is random but the same on every call and only changes as items arrive.
    /// </summary>
    public async Task<IReadOnlyList<MapItem>> ItemsAsync(DateTimeOffset since, int sample, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<MapItem>(new CommandDefinition(
            """
            with pick as (
                select i.id from items i join votes v on v.item_id = i.id where i.embedding is not null
                union
                (select i.id from items i
                 where i.embedding is not null and i.published_at >= @since and not exists (select 1 from votes v where v.item_id = i.id)
                 order by md5('taste-map' || i.id::text) limit @sample)
            ), shown as (
                select distinct unnest(item_ids) as id from digests where status = 'sent'
            )
            select i.id, i.title, i.embedding::real[] as embedding, v.value::int as vote, coalesce(v.weight::double precision, 1) as weight,
                   v.at as voted_at, s.id is not null as shown
            from pick p
            join items i on i.id = p.id
            left join votes v on v.item_id = i.id
            left join shown s on s.id = i.id
            order by i.id
            """, new { since = since.UtcDateTime, sample }, cancellationToken: ct))).ToList();
    }
}
