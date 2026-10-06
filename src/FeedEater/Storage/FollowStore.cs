using Dapper;

namespace FeedEater.Storage;

public sealed record ActiveFollow
{
    public long Id { get; init; }
    public long RootItemId { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime EndsAt { get; init; }
    public long? RootMessageId { get; init; }
    public int Sent { get; init; }
}

public sealed record FollowMember
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Summary { get; init; } = "";
}

public sealed class FollowStore(FeedDb db)
{
    private const string Columns = "f.id, f.root_item_id, r.title, r.url, f.started_at, f.ends_at, f.root_message_id, f.sent";

    public async Task<IReadOnlyList<ActiveFollow>> ActiveAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ActiveFollow>(new CommandDefinition(
            $"select {Columns} from follows f join items r on r.id = f.root_item_id where f.status = 'active' order by f.id", cancellationToken: ct))).ToList();
    }

    public async Task<ActiveFollow?> ActiveAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ActiveFollow>(new CommandDefinition(
            $"select {Columns} from follows f join items r on r.id = f.root_item_id where f.id = @id and f.status = 'active'", new { id }, cancellationToken: ct));
    }

    /// <summary>True when an active follow is on the same story as the item: the same cluster head.</summary>
    public async Task<bool> FollowsStoryAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1 from follows f
                join items r on r.id = f.root_item_id
                join items n on n.id = @itemId
                where f.status = 'active' and coalesce(r.cluster_of, r.id) = coalesce(n.cluster_of, n.id))
            """, new { itemId }, cancellationToken: ct));
    }

    public async Task<long> StartAsync(long rootItemId, DateTime startedAt, DateTime endsAt, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "insert into follows (root_item_id, started_at, ends_at) values (@rootItemId, @startedAt, @endsAt) returning id",
            new { rootItemId, startedAt, endsAt }, cancellationToken: ct));
    }

    public async Task SetRootMessageAsync(long id, long messageId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update follows set root_message_id = @messageId where id = @id", new { id, messageId }, cancellationToken: ct));
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("delete from follows where id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Items ingested during the follow whose cluster head is the root's, plus unclustered ones at or above the similarity to the root or to any item
    /// already sent; oldest first, none twice.
    /// </summary>
    public async Task<IReadOnlyList<long>> MatchesAsync(long id, double similarity, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<long>(new CommandDefinition(
            """
            select i.id
            from follows f
            join items r on r.id = f.root_item_id
            join items i on i.clustered and i.id <> r.id and i.ingested_at >= f.started_at and i.ingested_at <= f.ends_at
            left join feeds fd on fd.id = i.feed_id
            where f.id = @id and coalesce(fd.muted, false) = false
              and not exists (select 1 from follow_items x where x.follow_id = f.id and x.item_id = i.id)
              and (i.cluster_of = coalesce(r.cluster_of, r.id)
                   or (i.cluster_of is null and i.embedding is not null
                       and (1 - (i.embedding <=> r.embedding) >= @similarity
                            or exists (select 1 from follow_items x join items s on s.id = x.item_id
                                       where x.follow_id = f.id and s.embedding is not null and 1 - (i.embedding <=> s.embedding) >= @similarity))))
            order by i.published_at, i.id
            limit @limit
            """, new { id, similarity, limit }, cancellationToken: ct))).ToList();
    }

    public async Task AddSentAsync(long id, long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            with added as (insert into follow_items (follow_id, item_id) values (@id, @itemId) on conflict do nothing returning 1)
            update follows set sent = sent + (select count(*) from added)::int, last_item_id = @itemId where id = @id
            """, new { id, itemId }, cancellationToken: ct));
    }

    /// <summary>Claims the close: true for the one caller that moved the follow from active to closed.</summary>
    public async Task<bool> CloseAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("update follows set status = 'closed' where id = @id and status = 'active'", new { id }, cancellationToken: ct)) == 1;
    }

    public async Task ReopenAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update follows set status = 'active' where id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>The root first, then the items sent in order, at most <paramref name="limit"/>; the summary is the read's, else the start of the text.</summary>
    public async Task<IReadOnlyList<FollowMember>> MembersAsync(long id, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FollowMember>(new CommandDefinition(
            """
            select i.id, i.title, i.url, coalesce(rd.summary, left(i.content, 300)) as summary
            from follows f
            join items i on i.id = f.root_item_id or i.id in (select x.item_id from follow_items x where x.follow_id = f.id)
            left join reads rd on rd.item_id = i.id
            left join follow_items fx on fx.follow_id = f.id and fx.item_id = i.id
            where f.id = @id
            order by (i.id = f.root_item_id) desc, fx.at, i.id
            limit @limit
            """, new { id, limit }, cancellationToken: ct))).ToList();
    }
}
