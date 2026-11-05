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

/// <summary>A live <c>has_feature</c> fact: which tool has which feature entity (not yet followed through <c>merged_into</c>).</summary>
public sealed record FeatureFactRow
{
    public Guid FactId { get; init; }
    public Guid ToolId { get; init; }
    public string ToolName { get; init; } = "";
    public string? ToolRef { get; init; }
    public Guid FeatureId { get; init; }
    public string Text { get; init; } = "";
    public string Locator { get; init; } = "{}";
}

public sealed record FeatureEntity
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? ExternalRef { get; init; }
    public Guid? MergedInto { get; init; }
}

/// <summary>A live <c>same_as</c> fact: <see cref="LoserName"/> was merged into <see cref="SurvivorName"/>.</summary>
public sealed record FeatureMerge
{
    public Guid FactId { get; init; }
    public Guid LoserId { get; init; }
    public string LoserName { get; init; } = "";
    public Guid SurvivorId { get; init; }
    public string SurvivorName { get; init; } = "";
    public DateTime RecordedAt { get; init; }
}

public sealed record CodeEvidence
{
    public Guid FactId { get; init; }
    public string Repo { get; init; } = "";
    public string Commit { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Lines { get; init; }
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

    public Task<Guid> PackageAsync(string name, string purlNoVersion, CancellationToken ct) => Facts.EnsureEntityAsync(PriorArtSchema.EntityPackage, name, purlNoVersion, ct);

    public Task<Guid> FeatureAsync(string slug, CancellationToken ct) => Facts.EnsureEntityAsync(PriorArtSchema.EntityFeature, slug.Replace('-', ' '), "feature:" + slug, ct);

    /// <summary>Whether this exact episode (source kind, ref and content hash) is already recorded: a caller's cheap check before it spends a model call again.</summary>
    public async Task<bool> HasEpisodeAsync(string sourceKind, string sourceRef, string contentHash, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from facts.episodes where namespace = @ns and source_kind = @sourceKind and source_ref = @sourceRef and content_hash = @contentHash)",
            new { ns = Ns, sourceKind, sourceRef, contentHash }, cancellationToken: ct));
    }

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

    /// <summary>The tool's live <c>latest_release</c> value: the release it was last read at.</summary>
    public async Task<string?> LatestReleaseAsync(string toolRef, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            select f.object_value from facts.facts f join facts.entities t on t.id = f.subject_id
            where f.namespace = @ns and f.predicate = 'latest_release' and f.retired_at is null and t.external_ref = @toolRef
            order by f.recorded_at desc limit 1
            """, new { ns = Ns, toolRef }, cancellationToken: ct));
    }

    /// <summary>Whether any version of this source is recorded, whatever its content hash.</summary>
    public async Task<bool> HasSourceAsync(string sourceKind, string sourceRef, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from facts.episodes where namespace = @ns and source_kind = @sourceKind and source_ref = @sourceRef)",
            new { ns = Ns, sourceKind, sourceRef }, cancellationToken: ct));
    }

    /// <summary>Every <c>has_feature</c> fact the base held at <paramref name="at"/>, with its first source's locator.</summary>
    public async Task<IReadOnlyList<FeatureFactRow>> FeatureFactsAsync(DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeatureFactRow>(new CommandDefinition(
            """
            select f.id as fact_id, t.id as tool_id, t.name as tool_name, t.external_ref as tool_ref, f.object_id as feature_id, f.text,
                   coalesce((select fs.locator::text from facts.fact_sources fs join facts.episodes e on e.id = fs.episode_id
                             where fs.fact_id = f.id order by e.recorded_at, e.id limit 1), '{}') as locator
            from facts.facts f join facts.entities t on t.id = f.subject_id
            where f.namespace = @ns and f.predicate = 'has_feature' and f.object_id is not null
              and f.valid @> @at::timestamptz and f.recorded_at <= @at and (f.retired_at is null or f.retired_at > @at)
            order by t.name, f.text
            """, new { ns = Ns, at = at.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    /// <summary>Every <c>uses</c> fact the base held at <paramref name="at"/>.</summary>
    public async Task<IReadOnlyList<ApproachUse>> UsesAtAsync(DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ApproachUse>(new CommandDefinition(
            """
            select a.id as approach_id, a.name as approach_name, t.id as tool_id, t.name as tool_name, t.external_ref as tool_ref, f.id as fact_id
            from facts.facts f join facts.entities a on a.id = f.object_id join facts.entities t on t.id = f.subject_id
            where f.namespace = @ns and f.predicate = 'uses' and f.valid @> @at::timestamptz and f.recorded_at <= @at and (f.retired_at is null or f.retired_at > @at)
            order by t.name
            """, new { ns = Ns, at = at.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<FeatureEntity>> FeatureEntitiesAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeatureEntity>(new CommandDefinition(
            "select id, name, external_ref, merged_into from facts.entities where namespace = @ns and kind = @kind order by name",
            new { ns = Ns, kind = PriorArtSchema.EntityFeature }, cancellationToken: ct))).ToList();
    }

    /// <summary>Feature pairs already compared (merged or found different), both orders.</summary>
    public async Task<ISet<(Guid, Guid)>> DecidedPairsAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<(Guid, Guid)>(new CommandDefinition(
            "select subject_id, object_id from facts.facts where namespace = @ns and predicate = any(@predicates) and retired_at is null and object_id is not null",
            new { ns = Ns, predicates = new[] { PriorArtSchema.SameAs, PriorArtSchema.DistinctFrom } }, cancellationToken: ct));
        var pairs = new HashSet<(Guid, Guid)>();
        foreach (var (a, b) in rows)
        {
            pairs.Add((a, b));
            pairs.Add((b, a));
        }

        return pairs;
    }

    public async Task<IReadOnlyList<FeatureMerge>> MergesAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeatureMerge>(new CommandDefinition(
            """
            select f.id as fact_id, l.id as loser_id, l.name as loser_name, s.id as survivor_id, s.name as survivor_name, f.recorded_at
            from facts.facts f join facts.entities l on l.id = f.subject_id join facts.entities s on s.id = f.object_id
            where f.namespace = @ns and f.predicate = @same and f.retired_at is null order by f.recorded_at
            """, new { ns = Ns, same = PriorArtSchema.SameAs }, cancellationToken: ct))).ToList();
    }

    /// <summary>Points <paramref name="loser"/> at <paramref name="survivor"/> and gives the survivor the loser's name as an alias. The caller records the <c>same_as</c> fact.</summary>
    public async Task SetMergedAsync(Guid loser, Guid survivor, string loserName, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update facts.entities set merged_into = @survivor where id = @loser and namespace = @ns", new { loser, survivor, ns = Ns }, tx, cancellationToken: ct));
        var letters = new string(loserName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var skeleton = new string(letters.Where(ch => "aeiou".IndexOf(ch, StringComparison.Ordinal) < 0).ToArray());
        await c.ExecuteAsync(new CommandDefinition(
            "insert into facts.entity_aliases (entity_id, alias, key_phonetic, key_skeleton) values (@survivor, @loserName, @letters, @skeleton) on conflict do nothing",
            new { survivor, loserName, letters, skeleton }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Reverses <see cref="SetMergedAsync"/>: the loser stands alone again and the survivor loses the alias.</summary>
    public async Task ClearMergedAsync(Guid loser, Guid survivor, string loserName, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("update facts.entities set merged_into = null where id = @loser and namespace = @ns", new { loser, ns = Ns }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition("delete from facts.entity_aliases where entity_id = @survivor and alias = @loserName", new { survivor, loserName }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Up to <paramref name="count"/> random live facts whose source locator names a file at a commit.</summary>
    public async Task<IReadOnlyList<CodeEvidence>> SampleCodeEvidenceAsync(int count, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<CodeEvidence>(new CommandDefinition(
            """
            select f.id as fact_id, fs.locator->>'repo' as repo, fs.locator->>'commit' as commit, fs.locator->>'path' as path, fs.locator->>'lines' as lines
            from facts.facts f join facts.fact_sources fs on fs.fact_id = f.id
            where f.namespace = @ns and f.retired_at is null and fs.locator ? 'repo' and fs.locator ? 'commit' and fs.locator ? 'path'
            order by random() limit @count
            """, new { ns = Ns, count }, cancellationToken: ct))).ToList();
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
