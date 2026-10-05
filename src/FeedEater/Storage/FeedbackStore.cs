using Dapper;

namespace FeedEater.Storage;

public sealed record FeedVotes
{
    public long FeedId { get; init; }
    public int Up { get; init; }
    public int Down { get; init; }
}

public sealed record VoteCounts
{
    public int Up { get; init; }
    public int Down { get; init; }
}

public sealed record RatingTotals
{
    public int Up { get; init; }
    public int Down { get; init; }
    public int Ideas { get; init; }
}

public sealed record Idea
{
    public long ItemId { get; init; }
    public string PlaneProject { get; init; } = "";
    public string PlaneIssueId { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime At { get; init; }
}

public sealed record WeekReport
{
    public int Digests { get; init; }
    public int Highlights { get; init; }
    public int Up { get; init; }
    public int Down { get; init; }
    public IReadOnlyList<string> Liked { get; init; } = [];
    public IReadOnlyList<Idea> Ideas { get; init; } = [];
}

public sealed class FeedbackStore(FeedDb db)
{
    public async Task SetVoteAsync(long itemId, short value, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into votes (item_id, value, at) values (@itemId, @value, now())
            on conflict (item_id) do update set value = excluded.value, at = excluded.at
            """, new { itemId, value }, cancellationToken: ct));
    }

    public async Task ClearVoteAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("delete from votes where item_id = @itemId", new { itemId }, cancellationToken: ct));
    }

    public async Task<RatingTotals> TotalsAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleAsync<RatingTotals>(new CommandDefinition(
            """
            select (select count(*) from votes where value = 1)::int as up,
                   (select count(*) from votes where value = -1)::int as down,
                   (select count(*) from ideas)::int as ideas
            """, cancellationToken: ct));
    }

    /// <summary>Newest first: 👍 items and items filed as ideas (an item that is both counts once), and positive signals (Karakeep saves, GitHub stars).</summary>
    public Task<IReadOnlyList<float[]>> PositiveVectorsAsync(int limit, CancellationToken ct) => VectorsAsync(
        """
        select embedding, at from (
            select i.embedding::real[] as embedding, max(x.at) as at
            from (select item_id, at from votes where value = 1 union select item_id, at from ideas) x
            join items i on i.id = x.item_id
            where i.embedding is not null
            group by i.id
            union all
            select embedding::real[], at from signals where polarity = 1 and embedding is not null
        ) p
        order by at desc limit @limit
        """, limit, ct);

    public Task<IReadOnlyList<float[]>> NegativeVectorsAsync(int limit, CancellationToken ct) => VectorsAsync(
        """
        select i.embedding::real[] as embedding from votes v join items i on i.id = v.item_id
        where v.value = -1 and i.embedding is not null
        order by v.at desc limit @limit
        """, limit, ct);

    public async Task<IReadOnlyList<FeedVotes>> FeedVotesAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeedVotes>(new CommandDefinition(
            """
            select i.feed_id, (count(*) filter (where v.value = 1))::int as up, (count(*) filter (where v.value = -1))::int as down
            from votes v join items i on i.id = v.item_id
            where i.feed_id is not null
            group by i.feed_id
            """, cancellationToken: ct))).ToList();
    }

    public async Task<VoteCounts> VotesSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleAsync<VoteCounts>(new CommandDefinition(
            """
            select (count(*) filter (where value = 1))::int as up, (count(*) filter (where value = -1))::int as down
            from votes where at >= @since
            """, new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<WeekReport> WeekReportAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var args = new { since = since.UtcDateTime };
        var digests = await c.QuerySingleAsync<WeekReport>(new CommandDefinition(
            """
            select count(*)::int as digests, coalesce(sum(cardinality(item_ids)), 0)::int as highlights
            from digests where status = 'sent' and sent_at >= @since
            """, args, cancellationToken: ct));
        var votes = await VotesSinceAsync(since, ct);
        var liked = await c.QueryAsync<string>(new CommandDefinition(
            """
            select i.title from votes v join items i on i.id = v.item_id
            where v.value = 1 and v.at >= @since order by v.at desc limit 10
            """, args, cancellationToken: ct));
        var ideas = await c.QueryAsync<Idea>(new CommandDefinition(
            "select item_id, plane_project, plane_issue_id, title, at from ideas where at >= @since order by at",
            args, cancellationToken: ct));
        return digests with { Up = votes.Up, Down = votes.Down, Liked = liked.ToList(), Ideas = ideas.ToList() };
    }

    public async Task<Idea?> GetIdeaAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Idea>(new CommandDefinition(
            "select item_id, plane_project, plane_issue_id, title, at from ideas where item_id = @itemId",
            new { itemId }, cancellationToken: ct));
    }

    /// <summary>The first filing wins; a second one for the same item is ignored.</summary>
    public async Task AddIdeaAsync(Idea idea, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into ideas (item_id, plane_project, plane_issue_id, title, at)
            values (@ItemId, @PlaneProject, @PlaneIssueId, @Title, @At)
            on conflict (item_id) do nothing
            """, idea, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Idea>> IdeasAsync(string? planeProject, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Idea>(new CommandDefinition(
            """
            select item_id, plane_project, plane_issue_id, title, at from ideas
            where @planeProject::text is null or plane_project = @planeProject
            order by at desc limit @limit
            """, new { planeProject, limit }, cancellationToken: ct))).ToList();
    }

    private async Task<IReadOnlyList<float[]>> VectorsAsync(string sql, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<VectorRow>(new CommandDefinition(sql, new { limit }, cancellationToken: ct));
        return rows.Select(r => r.Embedding).ToList();
    }

    // Dapper maps arrays only as properties, not as a bare single-column result.
    private sealed record VectorRow
    {
        public float[] Embedding { get; init; } = [];
    }
}
