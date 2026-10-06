using Dapper;

namespace FeedEater.Storage;

public sealed record GoldenRow
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Feed { get; init; } = "";
    public string Content { get; init; } = "";
    public string? ExtraText { get; init; }
    public int Vote { get; init; }
    public int? OldRelevance { get; init; }
    public string? OldKind { get; init; }
    public string? OldSummary { get; init; }
}

public sealed class EvalStore(FeedDb db)
{
    /// <summary>Voted items, newest vote first, with what the prompts of the time said (triage and read rows, where they exist).</summary>
    public async Task<IReadOnlyList<GoldenRow>> GoldenAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<GoldenRow>(new CommandDefinition(
            """
            select i.id, i.title, coalesce(f.title, '') as feed, i.content, i.extra_text, v.value::int as vote,
                   t.relevance::int as old_relevance, coalesce(r.kind, t.kind) as old_kind, r.summary as old_summary
            from votes v join items i on i.id = v.item_id
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            left join reads r on r.item_id = i.id
            order by v.at desc
            """, cancellationToken: ct))).ToList();
    }
}
