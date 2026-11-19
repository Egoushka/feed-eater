using Dapper;
using FeedEater.Storage;

namespace FeedEater.Trials;

public sealed class TrialStore(FeedDb db)
{
    private const string Columns = "id, card_id, status, branch, repo, requested_at, started_at, finished_at, tests, benchmark, diff_stat, result_md";

    /// <summary>The new queued row, or null when the card already has one queued or running (the unique index decides, so two callers cannot both win).</summary>
    public async Task<TrialRow?> QueueAsync(long cardId, string repo, string branch, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var raw = await c.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition(
            $"insert into trial (card_id, branch, repo) values (@cardId, @branch, @repo) on conflict (card_id) where status in ('queued', 'running') do nothing returning {Columns}",
            new { cardId, branch, repo }, cancellationToken: ct));
        return raw?.ToRow();
    }

    public async Task<IReadOnlyList<TrialRow>> ListAsync(long? cardId, string? status, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Raw>(new CommandDefinition(
            $"select {Columns} from trial where (@cardId::bigint is null or card_id = @cardId) and (@status::text is null or status = @status) order by id desc limit @limit",
            new { cardId, status, limit }, cancellationToken: ct));
        return rows.Select(r => r.ToRow()).ToList();
    }

    /// <summary>Takes the oldest queued trial and marks it running; null when none waits.</summary>
    public async Task<TrialRow?> ClaimNextAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var raw = await c.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition(
            $"""
            update trial set status = 'running', started_at = now()
            where id = (select id from trial where status = 'queued' order by id limit 1 for update skip locked)
            returning {Columns}
            """, cancellationToken: ct));
        return raw?.ToRow();
    }

    public async Task FinishAsync(long id, string status, string tests, string benchmark, string diffStat, string resultMd, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update trial set status = @status, finished_at = now(), tests = @tests, benchmark = @benchmark, diff_stat = @diffStat, result_md = @resultMd where id = @id and status in ('queued', 'running')",
            new { id, status, tests, benchmark, diffStat, resultMd }, cancellationToken: ct));
    }

    /// <summary>A trial left running by a stopped process would block its card for good: after <paramref name="after"/> it is failed.</summary>
    public async Task<int> ExpireStaleAsync(TimeSpan after, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            """
            update trial set status = 'failed', finished_at = now(), result_md = 'Status: failed' || chr(10) || 'Reason: the runner stopped before this trial finished.'
            where status = 'running' and started_at < now() - @after
            """, new { after }, cancellationToken: ct));
    }

    private sealed record Raw
    {
        public long Id { get; init; }
        public long CardId { get; init; }
        public string Status { get; init; } = "";
        public string Branch { get; init; } = "";
        public string Repo { get; init; } = "";
        public DateTime RequestedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? FinishedAt { get; init; }
        public string Tests { get; init; } = "";
        public string Benchmark { get; init; } = "";
        public string DiffStat { get; init; } = "";
        public string ResultMd { get; init; } = "";

        public TrialRow ToRow() => new(
            Id, CardId, Status, Branch, Repo, Utc(RequestedAt), StartedAt is { } s ? Utc(s) : null, FinishedAt is { } f ? Utc(f) : null, Tests, Benchmark, DiffStat, ResultMd);

        private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);
    }
}
