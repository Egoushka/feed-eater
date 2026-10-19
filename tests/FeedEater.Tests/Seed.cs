using Dapper;
using FeedEater.Library;
using FeedEater.Signals;
using FeedEater.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FeedEater.Tests;

public static class Seed
{
    /// <summary>A library service for handlers under test; Karakeep answers an empty object and only matters when <c>Karakeep:Mirror</c> is on.</summary>
    public static LibraryService Library(PostgresFixture pg, IOptions<FeedEaterOptions> options, StubHandler? karakeep = null) => new(
        new LibraryStore(pg.Db), new ItemStore(pg.Db), new FeedbackStore(pg.Db),
        new KarakeepClient((karakeep ?? new StubHandler((_, _) => StubHandler.Json("""{"id":"bm-1"}"""))).Client("http://karakeep/")),
        options, TimeProvider.System, NullLogger<LibraryService>.Instance);

    public static async Task<long> ItemAsync(
        PostgresFixture pg, long feedId, string title, float[] embedding,
        DateTime? publishedAt = null, DateTime? ingestedAt = null, string content = "", long? entryId = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("insert into feeds (id, title) values (@feedId, @name) on conflict do nothing",
            new { feedId, name = $"Feed {feedId}" });
        return await c.ExecuteScalarAsync<long>(
            """
            insert into items (miniflux_entry_id, feed_id, url, canonical_url, title_hash, title, published_at, ingested_at, content, embedding)
            values (@entryId, @feedId, @url, @url, '', @title, @publishedAt, @ingestedAt, @content, @embedding::real[]::vector)
            returning id
            """,
            new
            {
                entryId,
                feedId,
                url = $"https://example.com/{Guid.NewGuid():N}",
                title,
                publishedAt = publishedAt ?? DateTime.UtcNow,
                ingestedAt = ingestedAt ?? DateTime.UtcNow,
                content,
                embedding,
            });
    }

    public static async Task SignalAsync(PostgresFixture pg, string externalId, float[] embedding)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            insert into signals (source, external_id, title, embedding, at)
            values ('karakeep', @externalId, 'saved', @embedding::real[]::vector, now())
            """, new { externalId, embedding });
    }

    public static async Task ReadAsync(PostgresFixture pg, long itemId, string project, string kind)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            "insert into reads (item_id, summary, why, kind, project, model) values (@itemId, 's', 'w', @kind, @project, 'm')",
            new { itemId, kind, project });
    }
}
