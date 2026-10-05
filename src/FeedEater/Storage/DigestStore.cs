using Dapper;

namespace FeedEater.Storage;

public sealed record DigestRow
{
    public string LocalDate { get; init; } = "";
    public string Status { get; init; } = "";
    public int Candidates { get; init; }
    public int Triaged { get; init; }
    public long[] ItemIds { get; init; } = [];
    public int SentCount { get; init; }
    public string? Note { get; init; }
    public string? Error { get; init; }
    public DateTime? SentAt { get; init; }
}

/// <summary>One row per local date (yyyy-MM-dd). A sent digest is never changed again.</summary>
public sealed class DigestStore(FeedDb db)
{
    private const string Columns = "local_date::text as local_date, status, candidates, triaged, item_ids, sent_count, note, error, sent_at";

    public async Task<DigestRow?> GetAsync(string date, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<DigestRow>(new CommandDefinition(
            $"select {Columns} from digests where local_date = @date::date", new { date }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DigestRow>> ListAsync(string? before, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<DigestRow>(new CommandDefinition(
            $"select {Columns} from digests where @before::date is null or local_date < @before::date order by local_date desc limit @limit",
            new { before, limit }, cancellationToken: ct))).ToList();
    }

    public Task CreateAsync(string date, CancellationToken ct) => ExecuteAsync(
        "insert into digests (local_date, status) values (@date::date, 'building') on conflict (local_date) do nothing", new { date }, ct);

    public Task SetSelectionAsync(string date, int candidates, int triaged, long[] itemIds, string? note, CancellationToken ct) => ExecuteAsync(
        """
        update digests set candidates = @candidates, triaged = @triaged, item_ids = @itemIds, note = @note, sent_count = 0
        where local_date = @date::date and status <> 'sent'
        """, new { date, candidates, triaged, itemIds, note }, ct);

    public Task SetSentCountAsync(string date, int sentCount, CancellationToken ct) => ExecuteAsync(
        "update digests set sent_count = @sentCount where local_date = @date::date", new { date, sentCount }, ct);

    public Task MarkSentAsync(string date, DateTimeOffset at, CancellationToken ct) => ExecuteAsync(
        "update digests set status = 'sent', sent_at = @at, error = null where local_date = @date::date",
        new { date, at = at.UtcDateTime }, ct);

    public Task MarkFailedAsync(string date, string error, CancellationToken ct) => ExecuteAsync(
        """
        insert into digests (local_date, status, error) values (@date::date, 'failed', @error)
        on conflict (local_date) do update set status = 'failed', error = excluded.error where digests.status <> 'sent'
        """, new { date, error }, ct);

    /// <summary>A digest still building on an earlier day missed its window: close it as failed, keeping its error.</summary>
    public Task CloseStaleAsync(string beforeDate, CancellationToken ct) => ExecuteAsync(
        """
        update digests set status = 'failed',
            note = case when note is null then 'not sent before 12:00' else note || E'\n' || 'not sent before 12:00' end,
            error = coalesce(error, 'not sent before 12:00')
        where status = 'building' and local_date < @beforeDate::date
        """, new { beforeDate }, ct);

    /// <summary>Records a failure while retries remain: the status stays, so the digest can still be resumed.</summary>
    public Task SetErrorAsync(string date, string error, CancellationToken ct) => ExecuteAsync(
        "update digests set error = @error where local_date = @date::date and status <> 'sent'", new { date, error }, ct);

    private async Task ExecuteAsync(string sql, object args, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }
}
