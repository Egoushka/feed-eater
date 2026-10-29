using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FeedEater.Storage;
using Testcontainers.PostgreSql;

namespace FeedEater.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg18-trixie").Build();

    public FeedDb Db { get; private set; } = null!;
    // Hosts built per test leave their pools behind (the app registers its data source as an instance), so idle connections are pruned fast.
    public string ConnectionString => _container.GetConnectionString() + ";Connection Idle Lifetime=3;Connection Pruning Interval=1";

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
            "truncate facts.fact_vectors, facts.fact_embeddings, facts.fact_sources, facts.retractions, facts.facts, facts.episodes, facts.entity_aliases, facts.entities, facts.predicates, api_tokens, swipe_acts, inbox, weekly_ideas, recall_reviews, recall_cards, recall_done, post_seeds, swipe_events, swipe_sessions, categories, duels, follow_items, follows, autopsy, snapshot_failures, repo_snapshots, feed_suggestions, saved, releases, weekly, llm_usage, cursors, profiles, signals, ideas, votes, digests, reads, triage, items, feeds restart identity cascade", c);
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
