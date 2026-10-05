using Dapper;
using FeedEater.Digest;

namespace FeedEater.Storage;

/// <summary>Model results per item, kept so a rerun never pays for the same item twice.</summary>
public sealed class AnalysisStore(FeedDb db)
{
    public async Task<TriageResult?> GetTriageAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<TriageResult>(new CommandDefinition(
            "select relevance::int as relevance, project, kind, reason from triage where item_id = @itemId",
            new { itemId }, cancellationToken: ct));
    }

    public async Task SaveTriageAsync(long itemId, TriageResult t, string model, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into triage (item_id, relevance, project, kind, reason, model) values (@itemId, @Relevance, @Project, @Kind, @Reason, @model)
            on conflict (item_id) do update set relevance = excluded.relevance, project = excluded.project, kind = excluded.kind,
                                                reason = excluded.reason, model = excluded.model, at = now()
            """, new { itemId, t.Relevance, t.Project, t.Kind, t.Reason, model }, cancellationToken: ct));
    }

    public async Task<ReadResult?> GetReadAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ReadResult>(new CommandDefinition(
            "select summary, why, kind, project, suggestion from reads where item_id = @itemId",
            new { itemId }, cancellationToken: ct));
    }

    public async Task SaveReadAsync(long itemId, ReadResult r, string model, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into reads (item_id, summary, why, kind, project, suggestion, model)
            values (@itemId, @Summary, @Why, @Kind, @Project, @Suggestion, @model)
            on conflict (item_id) do update set summary = excluded.summary, why = excluded.why, kind = excluded.kind,
                                                project = excluded.project, suggestion = excluded.suggestion, model = excluded.model, at = now()
            """, new { itemId, r.Summary, r.Why, r.Kind, r.Project, r.Suggestion, model }, cancellationToken: ct));
    }
}
