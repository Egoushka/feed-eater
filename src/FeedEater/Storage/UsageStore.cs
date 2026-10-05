using Dapper;
using FeedEater.Llm;

namespace FeedEater.Storage;

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

    public async Task<decimal> SpendSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "select coalesce(sum(cost), 0) from llm_usage where at >= @since",
            new { since = since.UtcDateTime }, cancellationToken: ct));
    }
}
