using System.Text.Json;
using Dapper;
using FeedEater.Storage;
using Switchknife.Facts;

namespace FeedEater.PriorArt;

public sealed record FactDetail
{
    public Guid Id { get; init; }
    public string Predicate { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTime RecordedAt { get; init; }
    public DateTime? ValidFrom { get; init; }
    public Guid SubjectId { get; init; }
    public string SubjectKind { get; init; } = "";
    public string SubjectName { get; init; } = "";
    public string? SubjectRef { get; init; }
    public Guid? ObjectId { get; init; }
    public string? ObjectKind { get; init; }
    public string? ObjectName { get; init; }
    public string? ObjectRef { get; init; }
}

public sealed record FactSourceRow
{
    public Guid FactId { get; init; }
    public string SourceKind { get; init; } = "";
    public string SourceRef { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string Trust { get; init; } = "";
    public string Locator { get; init; } = "{}";
    public string? Quote { get; init; }
    public DateTime RecordedAt { get; init; }
}

public sealed record ApproachUse
{
    public Guid ApproachId { get; init; }
    public string ApproachName { get; init; } = "";
    public Guid ToolId { get; init; }
    public string ToolName { get; init; } = "";
    public string? ToolRef { get; init; }
    public Guid FactId { get; init; }
}

public sealed record NamespaceStats(int LiveFacts, int RetiredFacts, int Entities, int Unembedded, IReadOnlyDictionary<string, int> EpisodesByKind, IReadOnlyDictionary<string, int> LiveByPredicate);

/// <summary>
/// The <c>prior-art</c> namespace of Switchknife.Facts in feed-eater's database: the library for writes and bi-temporal reads, plus the
/// read-only queries the library has no call for (entity names, a fact's sources in one round trip, who uses an approach).
/// </summary>
public sealed class PriorArtStore(FeedDb db, TimeProvider time)
{
    private const string Ns = PriorArtSchema.Namespace;

    private readonly SemaphoreSlim gate = new(1, 1);
    private bool ready;

    public FactsStore Facts { get; } = new(db.DataSource, Ns, time);

    /// <summary>Defines the predicates once; safe to call before every use.</summary>
    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (ready)
        {
            return;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (ready)
            {
                return;
            }

            foreach (var (name, cardinality) in PriorArtSchema.Predicates)
            {
                await Facts.DefinePredicateAsync(name, cardinality, ct);
            }

            ready = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<Guid> ToolAsync(string name, string externalRef, CancellationToken ct) => Facts.EnsureEntityAsync(PriorArtSchema.EntityTool, name, externalRef, ct);

    public Task<Guid> ProblemAsync(string slug, CancellationToken ct) => Facts.EnsureEntityAsync(PriorArtSchema.EntityProblem, slug.Replace('-', ' '), "problem:" + slug, ct);

    public Task<Guid> ApproachAsync(string slug, CancellationToken ct) => Facts.EnsureEntityAsync(PriorArtSchema.EntityApproach, slug.Replace('-', ' '), "approach:" + slug, ct);

    /// <summary>The id of the entity with this reference, or null: a lookup that creates nothing.</summary>
    public async Task<Guid?> FindAsync(string externalRef, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select id from facts.entities where namespace = @ns and external_ref = @externalRef", new { ns = Ns, externalRef }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FactDetail>> DetailsAsync(Guid[] ids, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FactDetail>(new CommandDefinition(
            """
            select f.id, f.predicate, f.text, f.recorded_at, lower(f.valid) as valid_from,
                   s.id as subject_id, s.kind as subject_kind, s.name as subject_name, s.external_ref as subject_ref,
                   o.id as object_id, o.kind as object_kind, o.name as object_name, o.external_ref as object_ref
            from facts.facts f
            join facts.entities s on s.id = f.subject_id
            left join facts.entities o on o.id = f.object_id
            where f.namespace = @ns and f.id = any(@ids)
            """, new { ns = Ns, ids }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<FactSourceRow>> SourcesAsync(Guid[] factIds, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FactSourceRow>(new CommandDefinition(
            """
            select fs.fact_id, e.source_kind, e.source_ref, e.content_hash, e.trust, fs.locator::text as locator, fs.quote, e.recorded_at
            from facts.fact_sources fs
            join facts.episodes e on e.id = fs.episode_id
            where fs.fact_id = any(@factIds) and e.namespace = @ns
            order by e.recorded_at, e.id
            """, new { ns = Ns, factIds }, cancellationToken: ct))).ToList();
    }

    /// <summary>Tools whose live <c>status</c> fact says anything but active.</summary>
    public async Task<ISet<Guid>> InactiveToolsAsync(Guid[] toolIds, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Guid>(new CommandDefinition(
            """
            select distinct subject_id from facts.facts
            where namespace = @ns and predicate = 'status' and subject_id = any(@toolIds) and object_value <> 'active'
              and valid @> @at::timestamptz and recorded_at <= @at and (retired_at is null or retired_at > @at)
            """, new { ns = Ns, toolIds, at = at.UtcDateTime }, cancellationToken: ct))).ToHashSet();
    }

    /// <summary>Every live <c>uses</c> fact whose object is one of these approaches.</summary>
    public async Task<IReadOnlyList<ApproachUse>> UsesAsync(Guid[] approachIds, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ApproachUse>(new CommandDefinition(
            """
            select a.id as approach_id, a.name as approach_name, t.id as tool_id, t.name as tool_name, t.external_ref as tool_ref, f.id as fact_id
            from facts.facts f
            join facts.entities a on a.id = f.object_id
            join facts.entities t on t.id = f.subject_id
            where f.namespace = @ns and f.predicate = 'uses' and f.retired_at is null and f.object_id = any(@approachIds)
            order by t.name
            """, new { ns = Ns, approachIds }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Gives every live fact without a vector for <paramref name="embedder"/>'s model one, in batches. Safe to repeat: it is also the
    /// retry after an embedder outage. Returns how many it embedded.
    /// </summary>
    public async Task<int> EmbedPendingAsync(IPriorArtEmbedder embedder, int batch, CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            List<(Guid Id, string Text)> rows;
            await using (var c = await db.DataSource.OpenConnectionAsync(ct))
            {
                rows = (await c.QueryAsync<(Guid, string)>(new CommandDefinition(
                    """
                    select f.id, f.text from facts.facts f
                    where f.namespace = @ns and f.retired_at is null and f.predicate <> all(@skip)
                      and not exists (select 1 from facts.fact_vectors v where v.fact_id = f.id and v.model = @model)
                    order by f.recorded_at, f.id limit @batch
                    """, new { ns = Ns, skip = PriorArtSchema.NotEmbedded.ToArray(), model = embedder.Model, batch }, cancellationToken: ct))).ToList();
            }

            if (rows.Count == 0)
            {
                return done;
            }

            var vectors = await embedder.EmbedAsync(rows.Select(r => r.Text).ToList(), ct);
            await Facts.SetEmbeddingsAsync(embedder.Model, rows.Select((r, i) => new FactEmbedding(r.Id, vectors[i])).ToList(), ct);
            done += rows.Count;
        }
    }

    public async Task<NamespaceStats> StatsAsync(string model, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var live = await c.ExecuteScalarAsync<int>(new CommandDefinition("select count(*) from facts.facts where namespace = @ns and retired_at is null", new { ns = Ns }, cancellationToken: ct));
        var retired = await c.ExecuteScalarAsync<int>(new CommandDefinition("select count(*) from facts.facts where namespace = @ns and retired_at is not null", new { ns = Ns }, cancellationToken: ct));
        var entities = await c.ExecuteScalarAsync<int>(new CommandDefinition("select count(*) from facts.entities where namespace = @ns", new { ns = Ns }, cancellationToken: ct));
        var unembedded = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            select count(*) from facts.facts f where f.namespace = @ns and f.retired_at is null and f.predicate <> all(@skip)
              and not exists (select 1 from facts.fact_vectors v where v.fact_id = f.id and v.model = @model)
            """, new { ns = Ns, skip = PriorArtSchema.NotEmbedded.ToArray(), model }, cancellationToken: ct));
        var episodes = (await c.QueryAsync<(string, int)>(new CommandDefinition(
            "select source_kind, count(*)::int from facts.episodes where namespace = @ns group by 1 order by 1", new { ns = Ns }, cancellationToken: ct))).ToDictionary(r => r.Item1, r => r.Item2);
        var byPredicate = (await c.QueryAsync<(string, int)>(new CommandDefinition(
            "select predicate, count(*)::int from facts.facts where namespace = @ns and retired_at is null group by 1 order by 1", new { ns = Ns }, cancellationToken: ct))).ToDictionary(r => r.Item1, r => r.Item2);
        return new NamespaceStats(live, retired, entities, unembedded, episodes, byPredicate);
    }

    /// <summary>The confidence a source line carries in its locator (0 to 1), or null.</summary>
    public static double? ConfidenceOf(string locatorJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(locatorJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("confidence", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
