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
    /// <summary>One slot per message part, in send order; 0 means a send was attempted but no id came back.</summary>
    public long[] SentMessageIds { get; init; } = [];
    public string? Note { get; init; }
    public string? Error { get; init; }
    public DateTime? SentAt { get; init; }
}

/// <summary>One row per local date (yyyy-MM-dd). A sent digest is never changed again.</summary>
public sealed class DigestStore(FeedDb db)
{
    private const string Columns =
        "local_date::text as local_date, status, candidates, triaged, item_ids, sent_count, sent_message_ids, note, error, sent_at";

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

    /// <summary>A forced run may rebuild a digest that failed without items; a sent one is never reopened.</summary>
    public Task ReopenAsync(string date, CancellationToken ct) => ExecuteAsync(
        "update digests set status = 'building', error = null where local_date = @date::date and status = 'failed'", new { date }, ct);

    public Task SetSelectionAsync(string date, int candidates, int triaged, long[] itemIds, string? note, CancellationToken ct) => ExecuteAsync(
        """
        update digests set candidates = @candidates, triaged = @triaged, item_ids = @itemIds, note = @note,
            sent_count = 0, sent_message_ids = '{}'
        where local_date = @date::date and status <> 'sent'
        """, new { date, candidates, triaged, itemIds, note }, ct);

    public Task SetSentCountAsync(string date, int sentCount, CancellationToken ct) => ExecuteAsync(
        "update digests set sent_count = @sentCount where local_date = @date::date", new { date, sentCount }, ct);

    /// <summary>A resend starts every part over: last run's send intents are no longer meaningful.</summary>
    public Task ResetSentAsync(string date, CancellationToken ct) => ExecuteAsync(
        "update digests set sent_count = 0, sent_message_ids = '{}' where local_date = @date::date", new { date }, ct);

    /// <summary>
    /// Written before the Telegram call for <paramref name="part"/>, so a crash before the message id comes back still
    /// leaves a trace: on a rerun, <see cref="GetAsync"/> shows this part attempted with id 0 (Telegram's real ids are
    /// always positive). Appends only when <paramref name="part"/> is exactly the next slot, so a retried call does
    /// not add a second placeholder.
    /// </summary>
    public Task RecordIntentAsync(string date, int part, CancellationToken ct) => ExecuteAsync(
        """
        update digests set sent_message_ids = sent_message_ids || 0::bigint
        where local_date = @date::date and coalesce(array_length(sent_message_ids, 1), 0) = @part
        """, new { date, part }, ct);

    /// <summary>The Telegram id for a part whose intent was already recorded; <c>sent_count</c> moves past it in the same write.</summary>
    public Task ConfirmSentAsync(string date, int part, long messageId, CancellationToken ct) => ExecuteAsync(
        "update digests set sent_message_ids[@index] = @messageId, sent_count = @sentCount where local_date = @date::date",
        new { date, index = part + 1, messageId, sentCount = part + 1 }, ct);

    /// <summary>
    /// Undoes <see cref="RecordIntentAsync"/> for a call that got a definite answer (Telegram refused or errored, not a
    /// crash): the next attempt should look exactly like the first, not like an attempt whose outcome is unknown.
    /// </summary>
    public Task DropIntentAsync(string date, int part, CancellationToken ct) => ExecuteAsync(
        """
        update digests set sent_message_ids = sent_message_ids[1:@part]
        where local_date = @date::date and array_length(sent_message_ids, 1) = @part + 1
        """, new { date, part }, ct);

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
