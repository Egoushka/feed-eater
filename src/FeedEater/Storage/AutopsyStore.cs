using System.Text.Json;
using Dapper;
using FeedEater.Hype;

namespace FeedEater.Storage;

/// <summary>A 👍 item that has no snapshot yet, with the text its repo link is looked for in.</summary>
public sealed record SnapshotCandidate(long ItemId, string Url, string Content);

/// <summary>A snapshot ready to be scored, with everything the autopsy knows about its item.</summary>
public sealed record AutopsyCandidate(
    RepoSnapshot Snapshot, string Title, string Url, string Feed, int? Relevance, IdeaOutcome Idea, bool Saved, int RelatedLater);

public sealed record AutopsyRow(string Month, DateTimeOffset BuiltAt, DateTimeOffset? SentAt, AutopsyReport Report, string Message);

/// <summary>Repo snapshots of 👍 items and the monthly autopsy built from them. Plain SQL: no model call.</summary>
public sealed class AutopsyStore(FeedDb db)
{
    /// <summary>Where the idea outcome comes from. The shipped-it work replaces the literal with the outcome stored on <c>ideas</c>.</summary>
    internal const string IdeaOutcomeSql = "case when exists (select 1 from ideas d where d.item_id = i.id) then 'Filed' else 'None' end";

    private const int ContentChars = 20000;

    private sealed record Raw
    {
        public string Month { get; init; } = "";
        public DateTime BuiltAt { get; init; }
        public DateTime? SentAt { get; init; }
        public string Report { get; init; } = "";
        public string Message { get; init; } = "";
    }

    private sealed record PendingRow
    {
        public long ItemId { get; init; }
        public string? Repo { get; init; }
        public DateTime TakenAt { get; init; }
        public int? Stars { get; init; }
        public DateTime? PushedAt { get; init; }
        public string? ReleaseTag { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string Title { get; init; } = "";
        public string Url { get; init; } = "";
        public string Feed { get; init; } = "";
        public int? Relevance { get; init; }
        public string IdeaOutcome { get; init; } = "None";
        public bool Saved { get; init; }
        public int RelatedLater { get; init; }
    }

    /// <summary>👍 items without a snapshot, oldest vote first.</summary>
    public async Task<IReadOnlyList<SnapshotCandidate>> CandidatesAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<SnapshotCandidate>(new CommandDefinition(
            $"""
            select i.id as item_id, i.url, left(i.content, {ContentChars}) as content
            from votes v join items i on i.id = v.item_id
            where v.value = 1 and not exists (select 1 from repo_snapshots s where s.item_id = i.id)
            order by v.at, i.id
            limit @limit
            """, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The first snapshot of an item stays; a second one for the same item is ignored.</summary>
    public async Task AddAsync(RepoSnapshot s, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into repo_snapshots (item_id, repo, taken_at, stars, pushed_at, release_tag, created_at)
            values (@ItemId, @Repo, @takenAt, @Stars, @pushedAt, @ReleaseTag, @createdAt)
            on conflict (item_id) do nothing
            """,
            new { s.ItemId, s.Repo, takenAt = s.TakenAt.UtcDateTime, s.Stars, pushedAt = s.PushedAt?.UtcDateTime, s.ReleaseTag, createdAt = s.CreatedAt?.UtcDateTime },
            cancellationToken: ct));
    }

    /// <summary>
    /// Snapshots at least <see cref="AutopsyRules.MinAgeDays"/> old and not older than <see cref="AutopsyRules.MaxAgeDays"/> that no earlier
    /// autopsy scored; those scored under <paramref name="month"/> come back too, so a run repeated for the same month gives the same report.
    /// "Related later" counts items from other feeds, published after the item, in its cluster or at or above <paramref name="clusterThreshold"/> cosine.
    /// </summary>
    public async Task<IReadOnlyList<AutopsyCandidate>> PendingAsync(string month, DateTimeOffset now, double clusterThreshold, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PendingRow>(new CommandDefinition(
            $"""
            select s.item_id, s.repo, s.taken_at, s.stars, s.pushed_at, s.release_tag, s.created_at,
                   i.title, i.url, coalesce(f.title, '') as feed, t.relevance::int as relevance,
                   {IdeaOutcomeSql} as idea_outcome,
                   exists (select 1 from saved sv where sv.item_id = i.id) as saved,
                   (select count(*) from items o
                    where o.id <> i.id and o.duplicate_of is null and o.feed_id is distinct from i.feed_id and o.published_at > i.published_at
                      and (coalesce(o.cluster_of, o.id) = coalesce(i.cluster_of, i.id)
                           or (o.embedding is not null and i.embedding is not null and 1 - (o.embedding <=> i.embedding) >= @clusterThreshold)))::int as related_later
            from repo_snapshots s
            join items i on i.id = s.item_id
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            where (s.autopsy_month is null or s.autopsy_month = @month::date)
              and s.taken_at <= @ripe and s.taken_at > @expired
            order by s.taken_at, s.item_id
            """,
            new
            {
                month, clusterThreshold,
                ripe = now.AddDays(-AutopsyRules.MinAgeDays).UtcDateTime, expired = now.AddDays(-AutopsyRules.MaxAgeDays).UtcDateTime,
            }, cancellationToken: ct));
        return rows.Select(r => new AutopsyCandidate(
            new RepoSnapshot(r.ItemId, r.Repo, Utc(r.TakenAt), r.Stars, r.PushedAt is { } p ? Utc(p) : null, r.ReleaseTag, r.CreatedAt is { } created ? Utc(created) : null),
            r.Title, r.Url, r.Feed, r.Relevance, Enum.Parse<IdeaOutcome>(r.IdeaOutcome), r.Saved, r.RelatedLater)).ToList();
    }

    /// <summary>Marks snapshots as scored by the autopsy of <paramref name="month"/>, so no later autopsy takes them again.</summary>
    public async Task MarkScoredAsync(IReadOnlyList<long> itemIds, string month, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update repo_snapshots set autopsy_month = @month::date where item_id = any(@itemIds)", new { month, itemIds = itemIds.ToArray() }, cancellationToken: ct));
    }

    /// <summary>Stores the autopsy; running the same month again replaces it and clears sent_at, so it is sent again.</summary>
    public async Task SaveAsync(string month, DateTimeOffset builtAt, AutopsyReport report, string message, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into autopsy (month, built_at, report, message) values (@month::date, @builtAt, @report::jsonb, @message)
            on conflict (month) do update set built_at = excluded.built_at, sent_at = null, report = excluded.report, message = excluded.message
            """, new { month, builtAt = builtAt.UtcDateTime, report = JsonSerializer.Serialize(report, Json.Options), message }, cancellationToken: ct));
    }

    public async Task MarkSentAsync(string month, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update autopsy set sent_at = @at where month = @month::date", new { month, at = at.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<AutopsyRow?> GetAsync(string month, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return Row(await c.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition(
            Select + " where month = @month::date", new { month }, cancellationToken: ct)));
    }

    public async Task<AutopsyRow?> LatestAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return Row(await c.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition(Select + " order by month desc limit 1", cancellationToken: ct)));
    }

    /// <summary>Month keys, newest first.</summary>
    public async Task<IReadOnlyList<string>> ListAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>(new CommandDefinition(
            "select month::text from autopsy order by month desc limit @limit", new { limit }, cancellationToken: ct))).ToList();
    }

    private const string Select = "select month::text as month, built_at, sent_at, report::text as report, message from autopsy";

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private static AutopsyRow? Row(Raw? r) => r is null
        ? null
        : new AutopsyRow(r.Month, Utc(r.BuiltAt), r.SentAt is { } s ? Utc(s) : null, JsonSerializer.Deserialize<AutopsyReport>(r.Report, Json.Options)!, r.Message);
}
