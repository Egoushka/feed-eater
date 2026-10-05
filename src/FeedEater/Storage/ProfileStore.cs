using Dapper;

namespace FeedEater.Storage;

public sealed record Profile
{
    public string Key { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? PlaneIdentifier { get; init; }
    public string Description { get; init; } = "";
    public float[] Embedding { get; init; } = [];
}

public sealed class ProfileStore(FeedDb db)
{
    /// <summary>Replaces the whole set, so keys removed from the profile file stop counting.</summary>
    public async Task ReplaceAllAsync(IReadOnlyList<Profile> profiles, DateTimeOffset builtAt, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("delete from profiles", transaction: tx, cancellationToken: ct));
        foreach (var p in profiles)
        {
            await c.ExecuteAsync(new CommandDefinition(
                """
                insert into profiles (key, kind, plane_identifier, description, embedding, built_at)
                values (@Key, @Kind, @PlaneIdentifier, @Description, @Embedding::real[]::vector, @builtAt)
                """,
                new { p.Key, p.Kind, p.PlaneIdentifier, p.Description, p.Embedding, builtAt = builtAt.UtcDateTime },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<Profile>> AllAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Profile>(new CommandDefinition(
            "select key, kind, plane_identifier, description, embedding::real[] as embedding from profiles order by kind, key",
            cancellationToken: ct))).ToList();
    }
}
