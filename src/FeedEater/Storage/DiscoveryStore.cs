using Dapper;

namespace FeedEater.Storage;

public sealed record LikedLink(long Id, string Title, string Url, string? LinkUrl);

public sealed record CachedSuggestion(string Domain, string? FeedUrl, string Status, DateTime CheckedAt);

public sealed class DiscoveryStore(FeedDb db)
{
    /// <summary>Items voted 👍 since the date, with the off-site link of a Reddit or HN post when there is one.</summary>
    public async Task<IReadOnlyList<LikedLink>> LikedLinksAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<LikedLink>(new CommandDefinition(
            """
            select i.id, i.title, i.url, i.link_url from votes v join items i on i.id = v.item_id
            where v.value = 1 and v.at >= @since order by v.at desc
            """, new { since = since.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyDictionary<string, CachedSuggestion>> CachedAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<CachedSuggestion>(new CommandDefinition(
            "select domain, feed_url, status, checked_at from feed_suggestions", cancellationToken: ct))).ToDictionary(s => s.Domain, StringComparer.Ordinal);
    }

    public async Task SaveAsync(string domain, string? feedUrl, string status, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into feed_suggestions (domain, feed_url, status, checked_at) values (@domain, @feedUrl, @status, @at)
            on conflict (domain) do update set feed_url = excluded.feed_url, status = excluded.status, checked_at = excluded.checked_at
            """, new { domain, feedUrl, status, at = at.UtcDateTime }, cancellationToken: ct));
    }
}
