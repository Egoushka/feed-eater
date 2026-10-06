using System.Text.Json;
using Dapper;

namespace FeedEater.Storage;

public sealed record WeeklyItem(long Id, string Title, string Url, string Feed);

public sealed record ProjectTally(string Project, int Up, int Down);

public sealed record FeedCount(long FeedId, string Title, int Count);

public sealed record WeeklyIdea(long ItemId, string Title, string PlaneProject);

public sealed record WeeklyReport(
    DateTimeOffset Since, DateTimeOffset Until, int Items, int Shown, int Up, int Down, int Ideas,
    IReadOnlyList<WeeklyItem> TopLiked, IReadOnlyList<ProjectTally> Projects, IReadOnlyList<WeeklyIdea> IdeasFiled,
    IReadOnlyList<FeedCount> TopFeeds, IReadOnlyList<FeedCount> MuteCandidates);

public sealed record WeeklyRow(string WeekOf, DateTimeOffset BuiltAt, DateTimeOffset? SentAt, WeeklyReport Report, string Message);

/// <summary>Pure SQL over the archive for the Sunday review, plus the stored result. No model call.</summary>
public sealed class WeeklyStore(FeedDb db)
{
    private sealed record Counts
    {
        public int Items { get; init; }
        public int Shown { get; init; }
        public int Up { get; init; }
        public int Down { get; init; }
        public int Ideas { get; init; }
    }

    private sealed record Raw
    {
        public string WeekOf { get; init; } = "";
        public DateTime BuiltAt { get; init; }
        public DateTime? SentAt { get; init; }
        public string Report { get; init; } = "";
        public string Message { get; init; } = "";
    }

    /// <summary>
    /// Figures for [since, until). Items are those published in the window (a backfill does not count), "shown" is the highlights of
    /// digests sent on the local dates the window touches. Mute candidates: unmuted feeds with posts in the window, none of them
    /// shown in a digest and none liked.
    /// </summary>
    public async Task<WeeklyReport> BuildAsync(DateTimeOffset since, DateTimeOffset until, string sinceDate, string untilDate, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var args = new { since = since.UtcDateTime, until = until.UtcDateTime, sinceDate, untilDate };
        var counts = await c.QuerySingleAsync<Counts>(new CommandDefinition(
            """
            select (select count(*) from items where published_at >= @since and published_at < @until and duplicate_of is null)::int as items,
                   (select coalesce(sum(cardinality(item_ids)), 0) from digests
                    where status = 'sent' and local_date between @sinceDate::date and @untilDate::date)::int as shown,
                   (select count(*) from votes where at >= @since and at < @until and value = 1)::int as up,
                   (select count(*) from votes where at >= @since and at < @until and value = -1)::int as down,
                   (select count(*) from ideas where at >= @since and at < @until)::int as ideas
            """, args, cancellationToken: ct));
        var top = await c.QueryAsync<WeeklyItem>(new CommandDefinition(
            """
            select i.id, i.title, i.url, coalesce(f.title, '') as feed
            from votes v join items i on i.id = v.item_id
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            where v.value = 1 and v.at >= @since and v.at < @until
            order by t.relevance desc nulls last, v.at desc, i.id
            limit 5
            """, args, cancellationToken: ct));
        var projects = await c.QueryAsync<ProjectTally>(new CommandDefinition(
            """
            select coalesce(r.project, t.project, i.profile_key, 'other') as project,
                   (count(*) filter (where v.value = 1))::int as up, (count(*) filter (where v.value = -1))::int as down
            from votes v join items i on i.id = v.item_id
            left join reads r on r.item_id = i.id
            left join triage t on t.item_id = i.id
            where v.at >= @since and v.at < @until
            group by 1 order by count(*) desc, 1
            """, args, cancellationToken: ct));
        var ideas = await c.QueryAsync<WeeklyIdea>(new CommandDefinition(
            "select item_id, title, plane_project from ideas where at >= @since and at < @until order by at, item_id", args, cancellationToken: ct));
        var feeds = await c.QueryAsync<FeedCount>(new CommandDefinition(
            """
            select f.id as feed_id, f.title, count(*)::int as count
            from votes v join items i on i.id = v.item_id join feeds f on f.id = i.feed_id
            where v.value = 1 and v.at >= @since and v.at < @until
            group by f.id, f.title order by count(*) desc, f.title limit 3
            """, args, cancellationToken: ct));
        var mute = await c.QueryAsync<FeedCount>(new CommandDefinition(
            """
            with shown as (
                select distinct unnest(item_ids) as id from digests where local_date between @sinceDate::date and @untilDate::date)
            select f.id as feed_id, f.title, count(i.id)::int as count
            from feeds f
            join items i on i.feed_id = f.id and i.published_at >= @since and i.published_at < @until and i.duplicate_of is null
            left join shown s on s.id = i.id
            left join votes v on v.item_id = i.id and v.value = 1 and v.at >= @since and v.at < @until
            where not f.muted
            group by f.id, f.title
            having count(s.id) = 0 and count(v.item_id) = 0
            order by count(i.id) desc, f.title
            limit 3
            """, args, cancellationToken: ct));
        return new WeeklyReport(since, until, counts.Items, counts.Shown, counts.Up, counts.Down, counts.Ideas,
            top.ToList(), projects.ToList(), ideas.ToList(), feeds.ToList(), mute.ToList());
    }

    /// <summary>Stores the review; running the same Sunday again replaces it and clears sent_at, so it is sent again.</summary>
    public async Task SaveAsync(string weekOf, DateTimeOffset builtAt, WeeklyReport report, string message, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into weekly (week_of, built_at, report, message) values (@weekOf::date, @builtAt, @report::jsonb, @message)
            on conflict (week_of) do update set built_at = excluded.built_at, sent_at = null, report = excluded.report, message = excluded.message
            """, new { weekOf, builtAt = builtAt.UtcDateTime, report = JsonSerializer.Serialize(report, Json.Options), message }, cancellationToken: ct));
    }

    public async Task MarkSentAsync(string weekOf, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update weekly set sent_at = @at where week_of = @weekOf::date", new { weekOf, at = at.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<WeeklyRow?> GetAsync(string weekOf, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return Row(await c.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition(
            Select + " where week_of = @weekOf::date", new { weekOf }, cancellationToken: ct)));
    }

    public async Task<WeeklyRow?> LatestAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return Row(await c.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition(Select + " order by week_of desc limit 1", cancellationToken: ct)));
    }

    /// <summary>Week dates, newest first.</summary>
    public async Task<IReadOnlyList<string>> ListAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>(new CommandDefinition(
            "select week_of::text from weekly order by week_of desc limit @limit", new { limit }, cancellationToken: ct))).ToList();
    }

    private const string Select = "select week_of::text as week_of, built_at, sent_at, report::text as report, message from weekly";

    private static WeeklyRow? Row(Raw? r) => r is null
        ? null
        : new WeeklyRow(
            r.WeekOf, new DateTimeOffset(DateTime.SpecifyKind(r.BuiltAt, DateTimeKind.Utc)),
            r.SentAt is { } s ? new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)) : null,
            JsonSerializer.Deserialize<WeeklyReport>(r.Report, Json.Options)!, r.Message);
}
