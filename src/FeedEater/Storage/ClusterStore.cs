using Dapper;

namespace FeedEater.Storage;

public sealed record PendingCluster(long Id, string Title);

public sealed record ClusterCandidate(long Id, string Title, double Similarity);

public sealed class ClusterStore(FeedDb db)
{
    /// <summary>Embedded, non-duplicate items not yet considered, oldest first so earlier items become heads before later ones look for them.</summary>
    public async Task<IReadOnlyList<PendingCluster>> PendingAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PendingCluster>(new CommandDefinition(
            "select id, title from items where not clustered and embedding is not null and duplicate_of is null order by published_at, id limit @limit",
            new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Heads (already considered, not members of another cluster) from other feeds, published within the window of the item,
    /// at or above the similarity, nearest first.
    /// </summary>
    public async Task<IReadOnlyList<ClusterCandidate>> NearestAsync(long id, double threshold, int windowDays, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ClusterCandidate>(new CommandDefinition(
            """
            select h.id, h.title, (1 - (h.embedding <=> i.embedding))::float8 as similarity
            from items i
            join items h on h.id <> i.id and h.clustered and h.cluster_of is null and h.duplicate_of is null and h.embedding is not null
                        and h.feed_id is distinct from i.feed_id
                        and h.published_at between i.published_at - make_interval(days => @windowDays) and i.published_at + make_interval(days => @windowDays)
            where i.id = @id and 1 - (h.embedding <=> i.embedding) >= @threshold
            order by h.embedding <=> i.embedding
            limit @limit
            """, new { id, threshold, windowDays, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Marks the item considered; <paramref name="head"/> null leaves it a head of its own.</summary>
    public async Task SetAsync(long id, long? head, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update items set cluster_of = @head, clustered = true where id = @id", new { id, head }, cancellationToken: ct));
    }
}
