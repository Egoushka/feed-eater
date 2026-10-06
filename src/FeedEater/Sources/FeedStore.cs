using Dapper;
using FeedEater.Storage;

namespace FeedEater.Sources;

/// <summary>A feed with its fetch state, for the poller and for /ui/sources.</summary>
public sealed record FeedState
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string? Category { get; init; }
    public string? SiteUrl { get; init; }
    public string FeedUrl { get; init; } = "";
    public string? Etag { get; init; }
    public string? LastModified { get; init; }
    public DateTime? LastFetchedAt { get; init; }
    public DateTime? NextFetchAt { get; init; }
    public int FailCount { get; init; }
    public string? LastError { get; init; }
}

/// <summary>The built-in reader's view of the <c>feeds</c> table: subscriptions and fetch state. Items go through <see cref="ItemStore"/>.</summary>
public sealed class FeedStore(FeedDb db)
{
    private const string Columns = "id, title, category, site_url, feed_url, etag, last_modified, last_fetched_at, next_fetch_at, fail_count, last_error";

    /// <summary>Adds a subscription; <c>Added</c> is false (and the id the existing one) when the URL is already subscribed.</summary>
    public async Task<(long Id, bool Added)> AddAsync(string title, string feedUrl, string? siteUrl, string? category, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var id = await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            insert into feeds (title, category, site_url, feed_url)
            select @title, @category, @siteUrl, @feedUrl where not exists (select 1 from feeds where lower(feed_url) = lower(@feedUrl))
            returning id
            """, new { title, category, siteUrl, feedUrl }, cancellationToken: ct));
        return id is { } added
            ? (added, true)
            : (await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "select id from feeds where lower(feed_url) = lower(@feedUrl) order by id limit 1", new { feedUrl }, cancellationToken: ct)), false);
    }

    /// <summary>Feeds with a URL whose next fetch has come, never-fetched ones first.</summary>
    public async Task<IReadOnlyList<FeedState>> DueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeedState>(new CommandDefinition(
            $"select {Columns} from feeds where feed_url is not null and (next_fetch_at is null or next_fetch_at <= @now) order by next_fetch_at nulls first, id limit @limit",
            new { now = now.UtcDateTime, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<FeedState?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<FeedState>(new CommandDefinition(
            $"select {Columns} from feeds where id = @id and feed_url is not null", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FeedState>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeedState>(new CommandDefinition(
            $"select {Columns} from feeds where feed_url is not null order by title", cancellationToken: ct))).ToList();
    }

    /// <summary>Feeds that failed at least <paramref name="failures"/> fetches in a row, worst first.</summary>
    public async Task<IReadOnlyList<FeedState>> FailingAsync(int failures, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeedState>(new CommandDefinition(
            $"select {Columns} from feeds where feed_url is not null and fail_count >= @failures order by fail_count desc, title", new { failures }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// A good fetch (including 304): the validators and next time are stored and the failure count is cleared. A feed that
    /// still carries its URL as the title (OPML import without a name) takes the title the feed gives.
    /// </summary>
    public async Task SucceededAsync(long id, string? etag, string? lastModified, string? title, string? siteUrl, DateTimeOffset now, DateTimeOffset next, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            update feeds set etag = @etag, last_modified = @lastModified, last_fetched_at = @now, next_fetch_at = @next, fail_count = 0, last_error = null,
                             title = case when @title is not null and @title <> '' and title in (feed_url, '') then @title else title end,
                             site_url = coalesce(site_url, @siteUrl)
            where id = @id
            """, new { id, etag, lastModified, title, siteUrl, now = now.UtcDateTime, next = next.UtcDateTime }, cancellationToken: ct));
    }

    /// <summary>last_fetched_at stays: it marks the last good fetch, and the backfill window applies until there is one.</summary>
    public async Task FailedAsync(long id, string error, DateTimeOffset next, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update feeds set next_fetch_at = @next, fail_count = fail_count + 1, last_error = @error where id = @id",
            new { id, error, next = next.UtcDateTime }, cancellationToken: ct));
    }

    /// <summary>
    /// Unsubscribes. The archive keeps the feed's items, now without a feed, so votes, ideas and search results stay; false when
    /// no such feed exists.
    /// </summary>
    public async Task<bool> RemoveAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update items set feed_id = null where feed_id = @id", new { id }, tx, cancellationToken: ct));
        var removed = await c.ExecuteAsync(new CommandDefinition("delete from feeds where id = @id", new { id }, tx, cancellationToken: ct)) > 0;
        await tx.CommitAsync(ct);
        return removed;
    }
}
