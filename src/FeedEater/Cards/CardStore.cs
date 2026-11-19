using System.Text.Json;
using Dapper;
using FeedEater.Storage;

namespace FeedEater.Cards;

public sealed record FeedHit
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public int Relevance { get; init; }
}

public sealed class CardStore(FeedDb db)
{
    private const string Columns =
        "id, term, title, what, repo, where_in_stack, evidence::text as evidence, evidence_checked, effort, gain, state, state_reason, created_at, state_changed_at, source_signal_ids";

    public async Task ReplaceInventoryAsync(IReadOnlyList<InventoryItem> found, IReadOnlyCollection<string> repos, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var i in found.DistinctBy(i => (i.Repo, i.Kind, i.Name, i.SourcePath)))
        {
            await c.ExecuteAsync(new CommandDefinition(
                """
                insert into inventory_item (kind, name, version, repo, source_path, seen_at) values (@Kind, @Name, @Version, @Repo, @SourcePath, @now)
                on conflict (repo, kind, name, source_path) do update set version = excluded.version, seen_at = excluded.seen_at
                """, new { i.Kind, i.Name, i.Version, i.Repo, i.SourcePath, now }, tx, cancellationToken: ct));
        }

        // Only repos read this run are pruned: a repo that could not be read keeps its last inventory.
        await c.ExecuteAsync(new CommandDefinition(
            "delete from inventory_item where repo = any(@repos) and seen_at < @now", new { repos = repos.ToArray(), now }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InventoryItem>> InventoryAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<InventoryItem>(new CommandDefinition(
            "select kind, name, version, repo, source_path as sourcepath from inventory_item order by repo, kind, name, source_path", cancellationToken: ct))).ToList();
    }

    /// <summary>Recent, ranked feed items whose title names the term; the owner's own reading verdict wins over the triage one.</summary>
    public async Task<IReadOnlyList<FeedHit>> HitsAsync(string term, DateTime since, int minRelevance, CancellationToken ct)
    {
        var like = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeedHit>(new CommandDefinition(
            """
            select i.id, i.title, i.url, i.published_at, coalesce(r.relevance, t.relevance)::int as relevance
            from items i
            join triage t on t.item_id = i.id
            left join reads r on r.item_id = i.id
            where i.published_at >= @since and i.duplicate_of is null and i.title ilike @like escape '\'
              and coalesce(r.relevance, t.relevance) >= @minRelevance
            order by 5 desc, i.published_at desc
            limit 10
            """, new { since, like, minRelevance }, cancellationToken: ct))).ToList();
    }

    /// <summary>Throws for an invalid card; false when the repo already has a card for the term, whatever its state (a held or dropped term does not come back).</summary>
    public async Task<bool> AddAsync(NewCard card, CancellationToken ct)
    {
        if (CardRules.Validate(card) is { } error)
        {
            throw new ArgumentException(error, nameof(card));
        }

        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            """
            insert into card (term, title, what, repo, where_in_stack, evidence, effort, gain, source_signal_ids)
            values (@Term, @Title, @What, @Repo, @WhereInStack, @evidence::jsonb, @Effort, @Gain, @ids)
            on conflict (repo, term) do nothing
            """,
            new { card.Term, card.Title, card.What, card.Repo, card.WhereInStack, evidence = JsonSerializer.Serialize(card.Evidence, Json.Options), card.Effort, card.Gain, ids = card.SourceSignalIds.ToArray() },
            cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<CardRow>> ListAsync(string? state, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<RawCard>(new CommandDefinition(
            $"select {Columns} from card where (@state::text is null or state = @state) order by created_at desc, id desc limit @limit", new { state, limit }, cancellationToken: ct));
        return rows.Select(r => r.ToRow()).ToList();
    }

    public async Task<CardRow?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<RawCard>(new CommandDefinition($"select {Columns} from card where id = @id", new { id }, cancellationToken: ct)))?.ToRow();
    }

    /// <summary>Every term that has a card in any state.</summary>
    public async Task<HashSet<string>> CardTermsAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>(new CommandDefinition("select distinct term from card", cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountsAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<(string State, int N)>(new CommandDefinition("select state, count(*)::int as n from card group by state", cancellationToken: ct)))
            .ToDictionary(r => r.State, r => r.N, StringComparer.Ordinal);
    }

    /// <summary>Sets the state and its reason. Throws <see cref="ArgumentException"/> for an unknown state or an empty reason; false when there is no such card.</summary>
    public async Task<bool> SetStateAsync(long id, string state, string? reason, CancellationToken ct)
    {
        if (CardRules.CheckTransition(state, reason) is { } error)
        {
            throw new ArgumentException(error, nameof(state));
        }

        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "update card set state = @state, state_reason = @reason, state_changed_at = now() where id = @id",
            new { id, state, reason = reason!.Trim() }, cancellationToken: ct)) > 0;
    }

    private sealed record RawCard
    {
        public long Id { get; init; }
        public string Term { get; init; } = "";
        public string Title { get; init; } = "";
        public string What { get; init; } = "";
        public string Repo { get; init; } = "";
        public string WhereInStack { get; init; } = "";
        public string Evidence { get; init; } = "[]";
        public bool EvidenceChecked { get; init; }
        public string Effort { get; init; } = "";
        public string Gain { get; init; } = "";
        public string State { get; init; } = "";
        public string StateReason { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime? StateChangedAt { get; init; }
        public long[] SourceSignalIds { get; init; } = [];

        public CardRow ToRow() => new(
            Id, Term, Title, What, Repo, WhereInStack, JsonSerializer.Deserialize<List<EvidenceLink>>(Evidence, Json.Options) ?? [], EvidenceChecked, Effort, Gain,
            State, StateReason, DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc), StateChangedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, SourceSignalIds);
    }
}
