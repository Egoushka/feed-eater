using System.Text;
using System.Text.Json;
using Dapper;
using DbUp;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FeedEater.Digest;
using FeedEater.Duels;
using FeedEater.Follow;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CombinationStorageTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_duel_vote_trains_at_half_weight_for_the_loser_and_a_later_vote_of_his_own_replaces_it()
    {
        var winner = await Seed.ItemAsync(pg, 1, "Winner", TestVectors.OneHot(1));
        var loser = await Seed.ItemAsync(pg, 1, "Loser", TestVectors.OneHot(2));
        var duels = new DuelStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var duel = await duels.CreateAsync(winner, loser, DateTimeOffset.UtcNow, default);

        await duels.AnswerAsync(duel, 'a', default);

        var up = Assert.Single(await feedback.PositiveWeightedAsync(10, default));
        var down = Assert.Single(await feedback.NegativeWeightedAsync(10, default));
        Assert.Equal(1, up.Weight);
        Assert.Equal(0.5, down.Weight);
        var labeled = await feedback.LabeledVectorsAsync(default);
        Assert.Equal([0.5, 1], labeled.Select(l => l.Weight).Order());

        await feedback.SetVoteAsync(loser, -1, default);   // the owner votes the same way himself: now a plain vote

        Assert.Equal(1, Assert.Single(await feedback.NegativeWeightedAsync(10, default)).Weight);
    }

    [Fact]
    public async Task A_vote_cast_by_hand_before_the_duel_is_answered_keeps_its_value_and_weight()
    {
        var first = await Seed.ItemAsync(pg, 1, "First", TestVectors.OneHot(1));
        var second = await Seed.ItemAsync(pg, 1, "Second", TestVectors.OneHot(2));
        var duels = new DuelStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var duel = await duels.CreateAsync(first, second, DateTimeOffset.UtcNow, default);
        await feedback.SetVoteAsync(second, 1, default, weight: 2);

        await duels.AnswerAsync(duel, 'a', default);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var votes = (await c.QueryAsync<(long Item, int Value, double Weight)>("select item_id, value::int, weight::double precision from votes order by item_id")).ToList();
        Assert.Equal([(first, 1, 1.0), (second, 1, 2.0)], votes);
    }

    [Fact]
    public async Task Reset_empties_every_table_the_migrations_create()
    {
        var a = await Seed.ItemAsync(pg, 1, "A", TestVectors.OneHot(1));
        var b = await Seed.ItemAsync(pg, 1, "B", TestVectors.OneHot(2));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var follow = await new FollowStore(pg.Db).StartAsync(a, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), default);
        await new FollowStore(pg.Db).AddSentAsync(follow, b, default);
        await new DuelStore(pg.Db).CreateAsync(a, b, DateTimeOffset.UtcNow, default);
        await c.ExecuteAsync("insert into repo_snapshots (item_id, repo, taken_at, stars) values (@a, 'o/r', now(), 3)", new { a });
        await new AutopsyStore(pg.Db).AddFailureAsync(b, default);
        await c.ExecuteAsync("insert into autopsy (month, built_at, report, message) values ('2026-10-01', now(), '{}'::jsonb, 'm')");

        await pg.ResetAsync();

        var tables = (await c.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE' and table_name <> 'schemaversions'")).ToList();
        Assert.Contains("duels", tables);
        Assert.Contains("follows", tables);
        Assert.Contains("follow_items", tables);
        Assert.Contains("repo_snapshots", tables);
        Assert.Contains("snapshot_failures", tables);
        Assert.Contains("autopsy", tables);
        foreach (var table in tables)
        {
            Assert.Equal(0, await c.ExecuteScalarAsync<long>($"select count(*) from \"{table}\""));
        }
    }

    private static string Database(string name, PostgresFixture pg) => new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = name, Pooling = false }.ConnectionString;

    private static IEnumerable<string> ScriptsUpTo(int last) => typeof(DatabaseMigrator).Assembly.GetManifestResourceNames()
        .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && int.Parse(n.Split(".Migrations.")[1][..4], System.Globalization.CultureInfo.InvariantCulture) <= last);

    private async Task<string> CreateAsync()
    {
        var name = $"migration_{Guid.NewGuid():N}";
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync($"create database {name}");
        return name;
    }

    private async Task DropAsync(string name)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync($"drop database {name} with (force)");
    }

    private static async Task<List<string>> TablesAsync(string connection)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        return (await c.QueryAsync<string>("select table_name from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE'")).ToList();
    }

    [Fact]
    public void The_migration_numbers_are_the_agreed_ones_with_0013_left_free()
    {
        var numbers = ScriptsUpTo(9999).Select(n => n.Split(".Migrations.")[1][..4]).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["0001", "0002", "0003", "0004", "0005", "0006", "0007", "0008", "0009", "0010", "0011", "0012", "0014", "0015", "0016", "0018", "0019", "0020", "0024", "0025", "0026", "0027", "0028", "0029", "0030", "0031", "0032", "0033", "0034", "0035", "0036", "0037"], numbers);
    }

    [Fact]
    public async Task Every_migration_applies_on_a_fresh_database()
    {
        var name = await CreateAsync();
        try
        {
            new DatabaseMigrator(Database(name, pg), NullLogger<DatabaseMigrator>.Instance).Run();

            var tables = await TablesAsync(Database(name, pg));
            Assert.All(new[] { "duels", "follows", "follow_items", "repo_snapshots", "snapshot_failures", "autopsy", "votes" }, t => Assert.Contains(t, tables));
        }
        finally
        {
            await DropAsync(name);
        }
    }

    [Fact]
    public async Task A_database_with_0001_to_0011_applied_takes_0012_and_0014_to_0037_and_keeps_its_rows()
    {
        var name = await CreateAsync();
        var connection = Database(name, pg);
        try
        {
            var old = DeployChanges.To.PostgresqlDatabase(connection)
                .WithScripts(ScriptsUpTo(11).Select(n =>
                {
                    using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(n)!;
                    return new DbUp.Engine.SqlScript(n, new StreamReader(stream).ReadToEnd());
                }))
                .WithTransactionPerScript().LogToNowhere().Build().PerformUpgrade();
            Assert.True(old.Successful);
            await using (var c = new NpgsqlConnection(connection))
            {
                await c.OpenAsync();
                await c.ExecuteAsync("insert into feeds (id, title) values (3, 'Feed')");
                await c.ExecuteAsync(
                    """
                    insert into items (feed_id, url, canonical_url, title_hash, title, published_at)
                    values (3, 'https://x.example/1', 'https://x.example/1', '', 'One', now()), (3, 'https://x.example/2', 'https://x.example/2', '', 'Two', now())
                    """);
                await c.ExecuteAsync("insert into votes (item_id, value) values (1, 1), (2, -1)");
            }

            new DatabaseMigrator(connection, NullLogger<DatabaseMigrator>.Instance).Run();

            await using var after = new NpgsqlConnection(connection);
            await after.OpenAsync();
            Assert.Equal<double>([1, 1], await after.QueryAsync<double>("select weight::double precision from votes order by item_id"));
            var tables = await TablesAsync(connection);
            Assert.All(new[] { "duels", "follows", "follow_items", "repo_snapshots", "snapshot_failures", "autopsy" }, t => Assert.Contains(t, tables));
            await after.ExecuteAsync("insert into duels (a, b) values (1, 2)");
            await after.ExecuteAsync("insert into follows (root_item_id, started_at, ends_at) values (1, now(), now())");
            Assert.Equal(["0012", "0014", "0015", "0016", "0018", "0019", "0020", "0024", "0025", "0026", "0027", "0028", "0029", "0030", "0031", "0032", "0033", "0034", "0035", "0036", "0037"], (await after.QueryAsync<string>("select scriptname from schemaversions order by scriptname"))
                .Select(s => s.Split(".Migrations.")[1][..4]).Where(n => string.CompareOrdinal(n, "0011") > 0));
        }
        finally
        {
            await DropAsync(name);
        }
    }

    [Fact]
    public async Task A_database_with_0001_to_0016_applied_takes_0018_to_0037_and_keeps_its_duels_and_snapshots()
    {
        var name = await CreateAsync();
        var connection = Database(name, pg);
        try
        {
            var old = DeployChanges.To.PostgresqlDatabase(connection)
                .WithScripts(ScriptsUpTo(16).Select(n =>
                {
                    using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(n)!;
                    return new DbUp.Engine.SqlScript(n, new StreamReader(stream).ReadToEnd());
                }))
                .WithTransactionPerScript().LogToNowhere().Build().PerformUpgrade();
            Assert.True(old.Successful);
            await using (var c = new NpgsqlConnection(connection))
            {
                await c.OpenAsync();
                await c.ExecuteAsync("insert into feeds (id, title) values (3, 'Feed')");
                await c.ExecuteAsync(
                    """
                    insert into items (feed_id, url, canonical_url, title_hash, title, published_at)
                    values (3, 'https://x.example/1', 'https://x.example/1', '', 'One', now()), (3, 'https://x.example/2', 'https://x.example/2', '', 'Two', now())
                    """);
                await c.ExecuteAsync("insert into duels (a, b) values (1, 2)");
                await c.ExecuteAsync("insert into repo_snapshots (item_id, repo, taken_at, stars) values (1, 'o/r', now(), 3)");
            }

            new DatabaseMigrator(connection, NullLogger<DatabaseMigrator>.Instance).Run();

            await using var after = new NpgsqlConnection(connection);
            await after.OpenAsync();
            Assert.Null(await after.ExecuteScalarAsync<DateTime?>("select send_failed_at from duels where a = 1"));
            Assert.Equal(1, await after.ExecuteScalarAsync<int>("select count(*)::int from repo_snapshots"));
            await after.ExecuteAsync("insert into snapshot_failures (item_id, failures) values (2, 1)");
            Assert.Equal(["0018", "0019", "0020", "0024", "0025", "0026", "0027", "0028", "0029", "0030", "0031", "0032", "0033", "0034", "0035", "0036", "0037"], (await after.QueryAsync<string>("select scriptname from schemaversions order by scriptname"))
                .Select(s => s.Split(".Migrations.")[1][..4]).Where(n => string.CompareOrdinal(n, "0016") > 0));
        }
        finally
        {
            await DropAsync(name);
        }
    }
}
