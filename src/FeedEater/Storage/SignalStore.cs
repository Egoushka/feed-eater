using Dapper;

namespace FeedEater.Storage;

public sealed record NewSignal(string Source, string ExternalId, string? Url, string Title, DateTime At);

public sealed class SignalStore(FeedDb db)
{
    public async Task<bool> ExistsAsync(string source, string externalId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from signals where source = @source and external_id = @externalId)",
            new { source, externalId }, cancellationToken: ct));
    }

    public async Task AddAsync(NewSignal signal, float[] embedding, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into signals (source, external_id, url, title, embedding, polarity, at)
            values (@Source, @ExternalId, @Url, @Title, @embedding::real[]::vector, 1, @At)
            on conflict (source, external_id) do nothing
            """,
            new { signal.Source, signal.ExternalId, signal.Url, signal.Title, embedding, signal.At }, cancellationToken: ct));
    }
}
