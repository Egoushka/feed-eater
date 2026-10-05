using Dapper;
using FeedEater.Digest;
using FeedEater.Ranking;

namespace FeedEater.Storage;

public sealed record Feed(long Id, string Title, string? Category, string? SiteUrl);

public sealed record NewItem(
    long EntryId, long FeedId, string Url, string CanonicalUrl, string TitleHash, string Title, DateTime PublishedAt, string Content);

public sealed record PendingEmbed
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed record Candidate
{
    public long Id { get; init; }
    public long? MinifluxEntryId { get; init; }
    public long? FeedId { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string FeedTitle { get; init; } = "";
    public string Content { get; init; } = "";
    public float[] Embedding { get; init; } = [];
}

public record ItemView
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public string? Category { get; init; }
    public DateTime PublishedAt { get; init; }
    public string Content { get; init; } = "";
    public string? ProfileKey { get; init; }
    public int? Relevance { get; init; }
    public string? Reason { get; init; }
    public string? Summary { get; init; }
    public string? Why { get; init; }
    public string? Kind { get; init; }
    public string? Project { get; init; }
    public string? Suggestion { get; init; }
    public int? Vote { get; init; }
    public string? FiledIn { get; init; }
}

/// <summary>An item with the time its vote or idea was recorded, for the feedback page.</summary>
public sealed record RatedItem : ItemView
{
    public DateTime RatedAt { get; init; }
}

public sealed record PostFilter(DateTimeOffset Since, string? Category, long? FeedId, string? Project, string? Kind, bool UnratedOnly, bool SummaryOnly);

/// <summary>Keyset position: microseconds since the Unix epoch plus the item id, so ties on the timestamp cannot repeat or skip rows.</summary>
public sealed record PageCursor(long Micros, long Id)
{
    public static PageCursor Of(DateTime utc, long id) => new((DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / 10, id);

    public static PageCursor? Parse(string? text) =>
        text?.Split('-') is [var micros, var id]
        && long.TryParse(micros, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m)
        && long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i)
            ? new PageCursor(m, i)
            : null;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Micros}-{Id}");
}

public sealed record SearchHit
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public string? Summary { get; init; }
    public string? Project { get; init; }
    public string? Kind { get; init; }
    public int? Vote { get; init; }
    public double Rank { get; init; }
}

public sealed record SourceStats
{
    public long FeedId { get; init; }
    public string Title { get; init; } = "";
    public string? Category { get; init; }
    public int Items { get; init; }
    public int Candidates { get; init; }
    public int Shown { get; init; }
    public int Up { get; init; }
    public int Down { get; init; }
}

public sealed class ItemStore(FeedDb db)
{
    public async Task UpsertFeedAsync(Feed feed, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into feeds (id, title, category, site_url) values (@Id, @Title, @Category, @SiteUrl)
            on conflict (id) do update set title = excluded.title, category = excluded.category, site_url = excluded.site_url
            """, feed, cancellationToken: ct));
    }

    /// <summary>
    /// Inserts one Miniflux entry; null when it is already stored. A row whose canonical URL, or whose title hash within
    /// 7 days, matches an earlier row points at it through duplicate_of. An empty URL or hash never matches.
    /// </summary>
    public async Task<long?> InsertAsync(NewItem item, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            insert into items (miniflux_entry_id, feed_id, url, canonical_url, title_hash, title, published_at, content, duplicate_of)
            values (@EntryId, @FeedId, @Url, @CanonicalUrl, @TitleHash, @Title, @PublishedAt, @Content,
                    coalesce(
                        (select id from items where @CanonicalUrl <> '' and canonical_url = @CanonicalUrl order by id limit 1),
                        (select id from items
                         where @TitleHash <> '' and title_hash = @TitleHash
                           and published_at between @PublishedAt - interval '7 days' and @PublishedAt + interval '7 days'
                         order by id limit 1)))
            on conflict (miniflux_entry_id) do nothing
            returning id
            """, item, cancellationToken: ct));
    }

    public async Task<long> MaxEntryIdAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "select coalesce(max(miniflux_entry_id), 0) from items", cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PendingEmbed>> UnembeddedAsync(int limit, long[] exclude, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PendingEmbed>(new CommandDefinition(
            "select id, title, content, url from items where embedding is null and id <> all(@exclude) order by id limit @limit",
            new { limit, exclude }, cancellationToken: ct))).ToList();
    }

    public async Task SetEmbeddingsAsync(IReadOnlyList<(long Id, float[] Vector)> rows, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var (id, vector) in rows)
        {
            await c.ExecuteAsync(new CommandDefinition(
                "update items set embedding = @vector::real[]::vector where id = @id", new { id, vector }, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Items published inside the window that are embedded, not duplicates and not triaged before <paramref name="triagedBefore"/>
    /// (the start of today). Same-day retries see today's triaged items again and reuse the stored result; items over the
    /// triage cap stay untriaged and can return while still inside the window.
    /// </summary>
    public async Task<IReadOnlyList<Candidate>> CandidatesAsync(DateTimeOffset publishedAfter, DateTimeOffset triagedBefore, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Candidate>(new CommandDefinition(
            """
            select i.id, i.miniflux_entry_id, i.feed_id, i.title, i.url, coalesce(f.title, '') as feed_title, i.content,
                   i.embedding::real[] as embedding
            from items i left join feeds f on f.id = i.feed_id
            where i.published_at > @publishedAfter and i.duplicate_of is null and i.embedding is not null
              and not exists (select 1 from triage t where t.item_id = i.id and t.at < @triagedBefore)
            """,
            new { publishedAfter = publishedAfter.UtcDateTime, triagedBefore = triagedBefore.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    public async Task SetScoresAsync(IReadOnlyList<Scored> scores, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            update items i set score = s.score, profile_key = s.key
            from unnest(@ids, @values, @keys) as s(id, score, key)
            where i.id = s.id
            """,
            new
            {
                ids = scores.Select(s => s.ItemId).ToArray(),
                values = scores.Select(s => (float)s.Score).ToArray(),
                keys = scores.Select(s => s.ProfileKey).ToArray(),
            }, cancellationToken: ct));
    }

    /// <summary>
    /// Gives every embedded item without a profile_key its nearest profile (cosine), so backfilled and older items match the
    /// project filter. Batched; a no-op without profiles. Returns how many rows were assigned.
    /// </summary>
    public async Task<int> AssignProfileKeysAsync(CancellationToken ct)
    {
        const int Batch = 1000;
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var total = 0;
        while (true)
        {
            var n = await c.ExecuteAsync(new CommandDefinition(
                """
                update items i set profile_key = (select p.key from profiles p order by p.embedding <=> i.embedding limit 1)
                where i.id in (select id from items where profile_key is null and embedding is not null and exists (select 1 from profiles) limit @batch)
                """, new { batch = Batch }, cancellationToken: ct));
            total += n;
            if (n < Batch)
            {
                return total;
            }
        }
    }

    public async Task SetContentAsync(long id, string content, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update items set content = @content where id = @id", new { id, content }, cancellationToken: ct));
    }

    public async Task<ItemView?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ItemView>(new CommandDefinition(
            """
            select i.id, i.title, i.url, coalesce(f.title, '') as feed, f.category, i.published_at, i.content, i.profile_key,
                   t.relevance::int as relevance, t.reason, r.summary, r.why, coalesce(r.kind, t.kind) as kind,
                   coalesce(r.project, t.project) as project, r.suggestion, v.value::int as vote, d.plane_project as filed_in
            from items i
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            left join reads r on r.item_id = i.id
            left join votes v on v.item_id = i.id
            left join ideas d on d.item_id = i.id
            where i.id = @id
            """, new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Per feed, over items published since <paramref name="since"/> (duplicates excluded): how many came in, were scored as
    /// candidates, appeared in any digest, and got each vote. Feeds with no items in the window still appear, with zeros.
    /// </summary>
    public async Task<IReadOnlyList<SourceStats>> SourceStatsAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<SourceStats>(new CommandDefinition(
            """
            with shown as (select distinct unnest(item_ids) as id from digests)
            select f.id as feed_id, f.title, f.category,
                   count(i.id)::int as items,
                   (count(i.id) filter (where i.score is not null))::int as candidates,
                   count(s.id)::int as shown,
                   (count(v.item_id) filter (where v.value = 1))::int as up,
                   (count(v.item_id) filter (where v.value = -1))::int as down
            from feeds f
            left join items i on i.feed_id = f.id and i.published_at >= @since and i.duplicate_of is null
            left join shown s on s.id = i.id
            left join votes v on v.item_id = i.id
            group by f.id, f.title, f.category
            order by f.title
            """, new { since = since.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    private const string CardColumns =
        """
        i.id, i.title, i.url, coalesce(f.title, '') as feed, f.category, i.published_at, left(i.content, 600) as content, i.profile_key,
        t.relevance::int as relevance, t.reason, r.summary, r.why, coalesce(r.kind, t.kind) as kind,
        coalesce(r.project, t.project) as project, r.suggestion, v.value::int as vote, d.plane_project as filed_in
        """;

    /// <summary>
    /// Posts newest first (published_at, then id), strictly after <paramref name="before"/>, duplicates excluded. Returns up to
    /// <paramref name="limit"/> rows; the text is clipped to 600 characters, enough for an excerpt.
    /// </summary>
    public async Task<IReadOnlyList<ItemView>> PostsAsync(PostFilter f, PageCursor? before, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ItemView>(new CommandDefinition(
            $"""
            select {CardColumns}
            from items i
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            left join reads r on r.item_id = i.id
            left join votes v on v.item_id = i.id
            left join ideas d on d.item_id = i.id
            where i.duplicate_of is null and i.published_at >= @since
              and (@micros::bigint is null or (i.published_at, i.id) < (timestamptz 'epoch' + @micros * interval '1 microsecond', @cursorId))
              and (@category::text is null or f.category = @category)
              and (@feedId::bigint is null or i.feed_id = @feedId)
              and (@project::text is null or i.profile_key = @project or coalesce(r.project, t.project) = @project)
              and (@kind::text is null or coalesce(r.kind, t.kind) = @kind)
              and (not @unrated or v.item_id is null)
              and (not @summaryOnly or r.item_id is not null)
            order by i.published_at desc, i.id desc
            limit @limit
            """,
            new
            {
                since = f.Since.UtcDateTime, micros = before?.Micros, cursorId = before?.Id ?? 0L, f.Category, f.FeedId, f.Project, f.Kind,
                unrated = f.UnratedOnly, summaryOnly = f.SummaryOnly, limit,
            }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Items already rated, newest rating first: 👍 or 👎 votes (<paramref name="rating"/> "up" or "down"), or filed ideas ("idea").
    /// </summary>
    public async Task<IReadOnlyList<RatedItem>> RatedAsync(string rating, PageCursor? before, int limit, CancellationToken ct)
    {
        var idea = rating == "idea";
        var at = idea ? "d.at" : "v.at";
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<RatedItem>(new CommandDefinition(
            $"""
            select {CardColumns}, {at} as rated_at
            from {(idea ? "ideas d join items i on i.id = d.item_id left join votes v on v.item_id = i.id"
                        : "votes v join items i on i.id = v.item_id left join ideas d on d.item_id = i.id")}
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            left join reads r on r.item_id = i.id
            where {(idea ? "true" : "v.value = @value")}
              and (@micros::bigint is null or ({at}, i.id) < (timestamptz 'epoch' + @micros * interval '1 microsecond', @cursorId))
            order by {at} desc, i.id desc
            limit @limit
            """,
            new { value = rating == "down" ? -1 : 1, micros = before?.Micros, cursorId = before?.Id ?? 0L, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>(new CommandDefinition(
            "select distinct category from feeds where category is not null and category <> '' order by category", cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<Feed>> FeedsAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Feed>(new CommandDefinition(
            "select id, title, category, site_url from feeds order by title", cancellationToken: ct))).ToList();
    }

    /// <summary>The digest's items in its order; items without a read result are skipped.</summary>
    public async Task<IReadOnlyList<DigestItem>> DigestItemsAsync(long[] ids, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<DigestItem>(new CommandDefinition(
            """
            select i.id, i.title, i.url, coalesce(f.title, '') as feed, r.project, r.kind, r.summary, r.why, r.suggestion
            from unnest(@ids::bigint[]) with ordinality as x(id, ord)
            join items i on i.id = x.id
            join reads r on r.item_id = i.id
            left join feeds f on f.id = i.feed_id
            order by x.ord
            """, new { ids }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Top 50 by vector distance and top 50 by full-text rank, merged by reciprocal rank fusion (k = 60).
    /// A null vector searches keywords only.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        float[]? vector, string text, string? project, string? kind, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct)
    {
        const string Filter =
            """
            i.duplicate_of is null
            and (@project::text is null or i.profile_key = @project
                 or exists (select 1 from reads r where r.item_id = i.id and r.project = @project))
            and (@kind::text is null or exists (select 1 from reads r where r.item_id = i.id and r.kind = @kind))
            and (@from::timestamptz is null or i.published_at >= @from)
            and (@to::timestamptz is null or i.published_at < @to)
            """;
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // Filters apply after the HNSW scan; iterative scan keeps scanning until the limit is met instead of stopping at ef_search.
        await c.ExecuteAsync(new CommandDefinition("set local hnsw.iterative_scan = relaxed_order", transaction: tx, cancellationToken: ct));
        return (await c.QueryAsync<SearchHit>(new CommandDefinition(
            $"""
            with v as (
                select id, row_number() over (order by dist) as pos from (
                    select i.id, i.embedding <=> @vector::real[]::vector as dist from items i
                    where @vector::real[] is not null and i.embedding is not null and {Filter}
                    order by dist limit 50) a
            ),
            t as (
                select id, row_number() over (order by score desc) as pos from (
                    select i.id, ts_rank(i.search, q) as score from items i, websearch_to_tsquery('simple', @text) q
                    where i.search @@ q and {Filter}
                    order by score desc limit 50) b
            ),
            fused as (
                select id, sum(1.0 / (60 + pos))::float8 as rrf from (select * from v union all select * from t) u group by id
            )
            select i.id, i.title, i.url, coalesce(f.title, '') as feed, i.published_at, r.summary,
                   coalesce(r.project, i.profile_key) as project, r.kind, vt.value::int as vote, fused.rrf as rank
            from fused
            join items i on i.id = fused.id
            left join feeds f on f.id = i.feed_id
            left join reads r on r.item_id = i.id
            left join votes vt on vt.item_id = i.id
            order by fused.rrf desc
            limit @limit
            """,
            new { vector, text, project, kind, from = from?.UtcDateTime, to = to?.UtcDateTime, limit }, transaction: tx, cancellationToken: ct))).ToList();
    }
}
