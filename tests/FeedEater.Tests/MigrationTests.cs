using Dapper;

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
}
