using Dapper;
using FeedEater.Llm;

namespace FeedEater.Storage;

public sealed record DaySpend
{
    public DateOnly Day { get; init; }
    public decimal Cost { get; init; }
    public long Tokens { get; init; }

    /// <summary>Calls that day whose cost is unknown; <see cref="Cost"/> leaves them out.</summary>
    public int Unpriced { get; init; }
}

public sealed record PurposeSpend
{
    public string Purpose { get; init; } = "";
    public string Model { get; init; } = "";
    public int Calls { get; init; }
    public decimal Cost { get; init; }
    public long Tokens { get; init; }
    public int Unpriced { get; init; }
}

public sealed class UsageStore(FeedDb db) : IUsageSink
{
    /// <summary>
    /// The purpose of the calls <c>doctor</c> and <c>/ui/setup</c> make. They are probes, not spend, and the page makes them on every load,
    /// so they are not stored (not cached for a minute instead: that would still add a row, with "cost unknown", every minute).
    /// </summary>
    public const string CheckPurpose = "doctor";

    public async Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal? cost, CancellationToken ct)
    {
        if (purpose == CheckPurpose)
        {
            return;
        }

        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into llm_usage (purpose, model, input_tokens, output_tokens, cost)
            values (@purpose, @model, @inputTokens, @outputTokens, @cost)
            """,
            new { purpose, model, inputTokens, outputTokens, cost }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DaySpend>> SpendByDayAsync(DateTimeOffset since, string timeZone, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<DaySpend>(new CommandDefinition(
            """
            select (at at time zone @timeZone)::date as day, coalesce(sum(cost), 0) as cost, sum(input_tokens + output_tokens)::bigint as tokens,
                   (count(*) filter (where cost is null))::int as unpriced
            from llm_usage where at >= @since group by 1 order by 1 desc
            """, new { since = since.UtcDateTime, timeZone }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<PurposeSpend>> SpendByPurposeAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PurposeSpend>(new CommandDefinition(
            """
            select purpose, model, count(*)::int as calls, coalesce(sum(cost), 0) as cost, sum(input_tokens + output_tokens)::bigint as tokens,
                   (count(*) filter (where cost is null))::int as unpriced
            from llm_usage where at >= @since group by purpose, model order by coalesce(sum(cost), 0) desc, purpose, model
            """, new { since = since.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    /// <summary>Calls since <paramref name="since"/> with an unknown cost, which <see cref="SpendSinceAsync"/> cannot count.</summary>
    public async Task<int> UnpricedSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from llm_usage where at >= @since and cost is null",
            new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<decimal> SpendSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "select coalesce(sum(cost), 0) from llm_usage where at >= @since",
            new { since = since.UtcDateTime }, cancellationToken: ct));
    }
}
