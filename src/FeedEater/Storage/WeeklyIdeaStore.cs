using Dapper;

namespace FeedEater.Storage;

public sealed record FiledIdea(string CanonicalUrl, string Week, string Title, string PlaneProject, string PlaneIssueId);

/// <summary>An item that may become an idea: why it is a candidate (<see cref="Signal"/>: liked, saved or improve) comes with its card.</summary>
public sealed record IdeaCandidate : ItemView
{
    public string Signal { get; init; } = "";
}

public sealed class WeeklyIdeaStore(FeedDb db)
{
    /// <summary>Liked, saved or kind=improve items since <paramref name="since"/>, the ones you acted on first.</summary>
    public async Task<IReadOnlyList<IdeaCandidate>> CandidatesAsync(DateTimeOffset since, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<IdeaCandidate>(new CommandDefinition(
            """
            select i.id, i.title, i.url, coalesce(f.title, '') as feed, i.published_at, left(i.content, 600) as content,
                   t.relevance::int as relevance, r.summary, r.why, coalesce(r.kind, t.kind) as kind,
                   coalesce(r.project, t.project) as project, r.suggestion,
                   case when v.value = 1 and v.at >= @since then 'liked'
                        when sv.at >= @since then 'saved'
                        else 'improve' end as signal
            from items i
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            left join reads r on r.item_id = i.id
            left join votes v on v.item_id = i.id
            left join saved sv on sv.item_id = i.id
            where i.duplicate_of is null
              and ((v.value = 1 and v.at >= @since)
                or sv.at >= @since
                or (coalesce(r.kind, t.kind) = 'improve' and r.suggestion is not null and i.published_at >= @since))
            order by (v.value = 1 and v.at >= @since) desc, (sv.at >= @since) desc, t.relevance desc nulls last, i.id desc
            limit @limit
            """, new { since = since.UtcDateTime, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Karakeep bookmarks saved since <paramref name="since"/> that have a link.</summary>
    public async Task<IReadOnlyList<NewSignal>> BookmarksAsync(DateTimeOffset since, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<NewSignal>(new CommandDefinition(
            """
            select source, external_id, url, title, at from signals
            where source = 'karakeep' and url is not null and at >= @since
            order by at desc limit @limit
            """, new { since = since.UtcDateTime, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlySet<string>> FiledUrlsAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var urls = await c.QueryAsync<string>(new CommandDefinition("select canonical_url from weekly_ideas", cancellationToken: ct));
        return urls.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> RecentTitlesAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>(new CommandDefinition(
            "select title from weekly_ideas order by at desc limit @limit", new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task AddAsync(FiledIdea idea, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into weekly_ideas (canonical_url, week, title, plane_project, plane_issue_id)
            values (@CanonicalUrl, @Week, @Title, @PlaneProject, @PlaneIssueId)
            on conflict (canonical_url) do nothing
            """, idea, cancellationToken: ct));
    }
}
