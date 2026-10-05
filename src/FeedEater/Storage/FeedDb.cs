using Dapper;
using Npgsql;

namespace FeedEater.Storage;

public sealed class FeedDb(NpgsqlDataSource dataSource)
{
    static FeedDb() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public NpgsqlDataSource DataSource => dataSource;

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            return await c.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: ct)) == 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }
}
