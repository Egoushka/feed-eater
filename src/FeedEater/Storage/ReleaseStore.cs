using Dapper;

namespace FeedEater.Storage;

public sealed record ReleaseRow
{
    public string Repo { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public long? ItemId { get; init; }
    public DateTime DetectedAt { get; init; }
    public bool Newer { get; init; }
    public string? Summary { get; init; }
    public string? Breaking { get; init; }
    public string? Evidence { get; init; }
    public bool Urgent { get; init; }
    public DateTime? AnnouncedAt { get; init; }
    public DateTime? DigestDate { get; init; }
}

public sealed record ReleaseItem(long Id, string Title, string Url, string Content);

public sealed class ReleaseStore(FeedDb db)
{
    private const string Columns =
        "repo, tag, version, title, url, item_id, detected_at, newer, summary, breaking, evidence, urgent, announced_at, digest_date::timestamp as digest_date";

    public async Task<bool> ExistsAsync(string repo, string tag, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from releases where repo = @repo and tag = @tag)", new { repo, tag }, cancellationToken: ct));
    }

    /// <summary>False when the release was already stored: each release is recorded, and announced, once.</summary>
    public async Task<bool> AddAsync(ReleaseRow r, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            """
            insert into releases (repo, tag, version, title, url, item_id, newer, summary, breaking, evidence, urgent)
            values (@Repo, @Tag, @Version, @Title, @Url, @ItemId, @Newer, @Summary, @Breaking, @Evidence, @Urgent)
            on conflict (repo, tag) do nothing
            """, r, cancellationToken: ct)) > 0;
    }

    public async Task MarkAnnouncedAsync(string repo, string tag, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update releases set announced_at = @at where repo = @repo and tag = @tag", new { repo, tag, at = at.UtcDateTime }, cancellationToken: ct));
    }

    /// <summary>
    /// The news not yet sent on its own, claimed for this digest date; asking again for the same date returns the same set,
    /// so a resumed digest does not change. Releases that arrive after the set was claimed wait for the next date.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseRow>> TakeForDigestAsync(string date, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            update releases set digest_date = @date::date
            where newer and announced_at is null and digest_date is null
              and not exists (select 1 from releases where digest_date = @date::date)
            """, new { date }, tx, cancellationToken: ct));
        var rows = (await c.QueryAsync<ReleaseRow>(new CommandDefinition(
            $"select {Columns} from releases where newer and announced_at is null and digest_date = @date::date order by detected_at, repo",
            new { date }, tx, cancellationToken: ct))).ToList();
        await tx.CommitAsync(ct);
        return rows;
    }

    public async Task<IReadOnlyList<ReleaseRow>> ListAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ReleaseRow>(new CommandDefinition(
            $"select {Columns} from releases order by detected_at desc, repo limit @limit", new { limit }, cancellationToken: ct))).ToList();
    }
}
