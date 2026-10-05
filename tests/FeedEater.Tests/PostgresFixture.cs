using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FeedEater.Storage;
using Testcontainers.PostgreSql;

namespace FeedEater.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg18-trixie").Build();

    public FeedDb Db { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        new DatabaseMigrator(ConnectionString, NullLogger<DatabaseMigrator>.Instance).Run();
        Db = new FeedDb(NpgsqlDataSource.Create(ConnectionString));
    }

    public async Task ResetAsync()
    {
        await using var c = await Db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "truncate llm_usage, cursors, profiles, signals, ideas, votes, digests, reads, triage, items, feeds restart identity cascade", c);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await Db.DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
