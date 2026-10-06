using Dapper;
using Npgsql;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Schema_has_pgvector_and_round_trips_an_embedding_as_a_real_array()
    {
        await pg.ResetAsync();
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var v = TestVectors.OneHot(3);
        await c.ExecuteAsync("insert into feeds (id, title) values (1, 'f')");

        var id = await c.ExecuteScalarAsync<long>(
            """
            insert into items (feed_id, url, canonical_url, title_hash, title, published_at, embedding)
            values (1, 'https://a.example/x', 'https://a.example/x', '', 'Title', now(), @v::real[]::vector)
            returning id
            """, new { v });
        var back = await c.ExecuteScalarAsync<float[]>("select embedding::real[] from items where id = @id", new { id });

        Assert.Equal(v, back);
        Assert.True(await pg.Db.PingAsync(default));
    }

    [Fact]
    public async Task Migration_0012_gives_existing_votes_weight_one_and_bounds_new_ones()
    {
        var name = $"migration_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Pooling = false }.ConnectionString;
        var target = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        await using (var c = new NpgsqlConnection(admin))
        {
            await c.OpenAsync();
            await c.ExecuteAsync($"create database {name}");
        }

        try
        {
            var scripts = typeof(DatabaseMigrator).Assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            async Task RunAsync(NpgsqlConnection c, string resource)
            {
                await using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(resource)!;
                await c.ExecuteAsync(await new StreamReader(stream).ReadToEndAsync());
            }

            await using var conn = new NpgsqlConnection(target);
            await conn.OpenAsync();
            foreach (var script in scripts.Where(s => !s.Contains("0012_", StringComparison.Ordinal)))
            {
                await RunAsync(conn, script);
            }

            await conn.ExecuteAsync("insert into feeds (id, title) values (3, 'Feed')");
            await conn.ExecuteAsync(
                """
                insert into items (feed_id, url, canonical_url, title_hash, title, published_at)
                values (3, 'https://x.example/1', 'https://x.example/1', '', 'One', now()),
                       (3, 'https://x.example/2', 'https://x.example/2', '', 'Two', now()),
                       (3, 'https://x.example/3', 'https://x.example/3', '', 'Three', now())
                """);
            await conn.ExecuteAsync("insert into votes (item_id, value) select id, case when id = 2 then -1 else 1 end from items where id < 3");

            await RunAsync(conn, scripts.Single(s => s.Contains("0012_", StringComparison.Ordinal)));

            Assert.Equal<double>([1, 1], await conn.QueryAsync<double>("select weight::double precision from votes order by item_id"));
            await conn.ExecuteAsync("insert into votes (item_id, value, weight) values (3, 1, 3)");
            await conn.ExecuteAsync("update votes set weight = 0.5 where item_id = 1");
            await Assert.ThrowsAsync<PostgresException>(async () => await conn.ExecuteAsync("update votes set weight = 0 where item_id = 1"));
            await Assert.ThrowsAsync<PostgresException>(async () => await conn.ExecuteAsync("update votes set weight = 3.5 where item_id = 1"));
        }
        finally
        {
            await using var c = new NpgsqlConnection(admin);
            await c.OpenAsync();
            await c.ExecuteAsync($"drop database {name} with (force)");
        }
    }
}
