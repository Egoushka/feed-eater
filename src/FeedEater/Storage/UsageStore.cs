using Dapper;
using FeedEater.Llm;

namespace FeedEater.Storage;

public sealed record DaySpend
{
    public DateOnly Day { get; init; }
    public decimal Cost { get; init; }
    public long Tokens { get; init; }
}

public sealed record PurposeSpend
{
    public string Purpose { get; init; } = "";
    public string Model { get; init; } = "";
    public int Calls { get; init; }
    public decimal Cost { get; init; }
}

public sealed class UsageStore(FeedDb db) : IUsageSink
{
    public async Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal cost, CancellationToken ct)
    {
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
            select (at at time zone @timeZone)::date as day, sum(cost) as cost, sum(input_tokens + output_tokens)::bigint as tokens
            from llm_usage where at >= @since group by 1 order by 1 desc
            """, new { since = since.UtcDateTime, timeZone }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<PurposeSpend>> SpendByPurposeAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PurposeSpend>(new CommandDefinition(
            """
            select purpose, model, count(*)::int as calls, sum(cost) as cost
            from llm_usage where at >= @since group by purpose, model order by sum(cost) desc, purpose, model
            """, new { since = since.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    public async Task<decimal> SpendSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "select coalesce(sum(cost), 0) from llm_usage where at >= @since",
            new { since = since.UtcDateTime }, cancellationToken: ct));
    }
}
