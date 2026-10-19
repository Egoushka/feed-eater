using Dapper;
using FeedEater.Inbox;
using FeedEater.Storage;

namespace FeedEater.Library;

public enum LibraryState { Unread, Read, All }

public sealed record LibraryRow(
    long ItemId, string Title, string? Url, string Feed, DateTime PublishedAt, DateTime SavedAt, DateTime? ReadAt, string? Note,
    string Source, string? Summary, bool FullText, bool Enriched);

public sealed record SavedRow(DateTime SavedAt, string Source, DateTime? ReadAt);

public sealed class LibraryStore(FeedDb db)
{
    public const int MaxEnrichAttempts = 5;

    /// <summary>False when the item was already saved; the first save keeps its source and time.</summary>
    public async Task<bool> AddAsync(long itemId, string source, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "insert into saved (item_id, source, at) values (@itemId, @source, @at) on conflict (item_id) do nothing",
            new { itemId, source, at = at.UtcDateTime }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> RemoveAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("delete from saved where item_id = @itemId", new { itemId }, cancellationToken: ct)) > 0;
    }

    public async Task<SavedRow?> GetAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<SavedRow>(new CommandDefinition(
            "select at as saved_at, source, read_at from saved where item_id = @itemId", new { itemId }, cancellationToken: ct));
    }

    public async Task<bool> IsSavedAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("select exists (select 1 from saved where item_id = @itemId)", new { itemId }, cancellationToken: ct));
    }

    public async Task SetKarakeepIdAsync(long itemId, string id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update saved set karakeep_id = @id where item_id = @itemId", new { itemId, id }, cancellationToken: ct));
    }

    public async Task<bool> MarkReadAsync(long itemId, bool read, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "update saved set read_at = case when @read then @at else null end where item_id = @itemId",
            new { itemId, read, at = at.UtcDateTime }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> SetNoteAsync(long itemId, string? note, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "update saved set note = @note where item_id = @itemId", new { itemId, note = string.IsNullOrWhiteSpace(note) ? null : note }, cancellationToken: ct)) > 0;
    }

    /// <summary>Newest save first; <paramref name="afterItemId"/> continues after that item's position.</summary>
    public async Task<IReadOnlyList<LibraryRow>> ListAsync(LibraryState state, long? afterItemId, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<LibraryRow>(new CommandDefinition(
            """
            select i.id as item_id, i.title, i.url, coalesce(f.title, '') as feed, i.published_at, s.at as saved_at, s.read_at, s.note, s.source,
                   r.summary, coalesce(i.extra_text, '') <> '' as full_text, s.enriched_at is not null as enriched
            from saved s
            join items i on i.id = s.item_id
            left join feeds f on f.id = i.feed_id
            left join reads r on r.item_id = i.id
            where (@state = 2 or (@state = 0 and s.read_at is null) or (@state = 1 and s.read_at is not null))
              and (@afterItemId::bigint is null or (s.at, s.item_id) < (select a.at, a.item_id from saved a where a.item_id = @afterItemId))
            order by s.at desc, s.item_id desc
            limit @limit
            """, new { state = (int)state, afterItemId, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Saved items still missing enrichment, oldest save first.</summary>
    public async Task<IReadOnlyList<long>> PendingEnrichmentAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<long>(new CommandDefinition(
            "select item_id from saved where enriched_at is null and enrich_attempts < @max order by at, item_id limit @limit",
            new { max = MaxEnrichAttempts, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<int> EnrichAttemptsAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition("select enrich_attempts from saved where item_id = @itemId", new { itemId }, cancellationToken: ct));
    }

    /// <summary>Counts one enrichment pass; <paramref name="done"/> closes the item.</summary>
    public async Task RecordAttemptAsync(long itemId, bool done, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update saved set enrich_attempts = enrich_attempts + 1, enriched_at = case when @done then now() else enriched_at end where item_id = @itemId",
            new { itemId, done }, cancellationToken: ct));
    }

    /// <summary>Unread items saved before the cutoff with no resurface entry in the last 30 days, oldest save first.</summary>
    public async Task<IReadOnlyList<long>> UnreadSinceAsync(DateTimeOffset savedBefore, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<long>(new CommandDefinition(
            """
            select s.item_id from saved s
            where s.read_at is null and s.at < @savedBefore
              and not exists (select 1 from inbox x where x.item_id = s.item_id and x.kind = @kind and x.created_at > now() - interval '30 days')
            order by s.at, s.item_id
            limit @limit
            """, new { savedBefore = savedBefore.UtcDateTime, kind = InboxKinds.Resurface, limit }, cancellationToken: ct))).ToList();
    }
}
