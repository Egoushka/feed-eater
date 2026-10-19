using System.Text.Json.Nodes;
using Dapper;
using FeedEater.Inbox;

namespace FeedEater.Storage;

public sealed class InboxStore(FeedDb db)
{
    private const string Columns = "id, key, kind, item_id, ref_id, payload::text as payload, created_at, expires_at, shown_session, done_at";

    /// <summary>Returns the existing row's id when the key was written before; a repeat refreshes <c>expires_at</c> and leaves <c>done_at</c> alone.</summary>
    public async Task<long> AddAsync(InboxEntry e, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            insert into inbox (key, kind, item_id, ref_id, payload, created_at, expires_at)
            values (@Key, @Kind, @ItemId, @RefId, @Payload::jsonb, @Now, @ExpiresAt)
            on conflict (key) do update set expires_at = excluded.expires_at
            returning id
            """,
            new { e.Key, e.Kind, e.ItemId, e.RefId, Payload = (e.Payload ?? []).ToJsonString(), Now = now.UtcDateTime, ExpiresAt = e.ExpiresAt?.UtcDateTime },
            cancellationToken: ct));
    }

    public async Task<InboxRow?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var raw = await c.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition($"select {Columns} from inbox where id = @id", new { id }, cancellationToken: ct));
        return raw?.ToRow();
    }

    /// <summary>Not done, not expired, oldest first.</summary>
    public async Task<IReadOnlyList<InboxRow>> OpenAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Raw>(new CommandDefinition(
            $"select {Columns} from inbox where done_at is null and (expires_at is null or expires_at > @now) order by created_at, id limit @limit",
            new { now = now.UtcDateTime, limit }, cancellationToken: ct))).Select(r => r.ToRow()).ToList();
    }

    /// <summary>False when it was already done.</summary>
    public async Task<bool> DoneAsync(long id, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "update inbox set done_at = @at where id = @id and done_at is null", new { id, at = at.UtcDateTime }, cancellationToken: ct)) == 1;
    }

    /// <summary>Releases a claim made with <see cref="DoneAsync"/> whose work failed, so the row can be decided again.</summary>
    public async Task ReopenAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update inbox set done_at = null where id = @id", new { id }, cancellationToken: ct));
    }

    private sealed record Raw(long Id, string Key, string Kind, long? ItemId, long? RefId, string Payload, DateTime CreatedAt, DateTime? ExpiresAt,
        long? ShownSession, DateTime? DoneAt)
    {
        public InboxRow ToRow() => new(Id, Key, Kind, ItemId, RefId, JsonNode.Parse(Payload)!.AsObject(), Utc(CreatedAt), ExpiresAt is { } e ? Utc(e) : null,
            ShownSession, DoneAt is { } d ? Utc(d) : null);

        private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));
    }
}
