using Dapper;

namespace FeedEater.Storage;

public sealed class CursorStore(FeedDb db)
{
    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select value from cursors where name = @name", new { name }, cancellationToken: ct));
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("delete from cursors where name = @name", new { name }, cancellationToken: ct));
    }

    public async Task SetAsync(string name, string value, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "insert into cursors (name, value) values (@name, @value) on conflict (name) do update set value = excluded.value",
            new { name, value }, cancellationToken: ct));
    }
}
