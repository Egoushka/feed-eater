using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FeedEater.Storage;

namespace FeedEater.Api;

public static class ApiScopes
{
    public const string App = "app";
}

public sealed record ApiTokenRow(long Id, string Name, string[] Scopes, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

/// <summary>Bearer tokens for the app API. Only the SHA-256 of a token is stored, so the plain token exists once, in the result of <see cref="CreateAsync"/>.</summary>
public sealed class ApiTokenStore(FeedDb db, TimeProvider time)
{
    private static readonly TimeSpan LastUsedEvery = TimeSpan.FromMinutes(1);

    public async Task<(long Id, string Token)> CreateAsync(string name, string[] scopes, CancellationToken ct)
    {
        var token = "fe_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "insert into api_tokens (name, hash, scopes) values (@name, @hash, @scopes) returning id",
            new { name, hash = Hash(token), scopes }, cancellationToken: ct));
        return (id, token);
    }

    public async Task<ApiTokenRow?> VerifyAsync(string token, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "select id, name, scopes, created_at, last_used_at, revoked_at from api_tokens where hash = @hash and revoked_at is null",
            new { hash = Hash(token) }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var found = row.ToRecord();

        var now = time.GetUtcNow().UtcDateTime;
        await c.ExecuteAsync(new CommandDefinition(
            "update api_tokens set last_used_at = @now where id = @id and (last_used_at is null or last_used_at < @minuteAgo)",
            new { now, id = row.Id, minuteAgo = now - LastUsedEvery }, cancellationToken: ct));
        return found;
    }

    public async Task<IReadOnlyList<ApiTokenRow>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Row>(new CommandDefinition(
            "select id, name, scopes, created_at, last_used_at, revoked_at from api_tokens order by id",
            cancellationToken: ct))).Select(r => r.ToRecord()).ToList();
    }

    public async Task<bool> RevokeAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "update api_tokens set revoked_at = now() where id = @id and revoked_at is null", new { id }, cancellationToken: ct)) > 0;
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string[] Scopes { get; init; } = [];
        public DateTime CreatedAt { get; init; }
        public DateTime? LastUsedAt { get; init; }
        public DateTime? RevokedAt { get; init; }

        public ApiTokenRow ToRecord() => new(Id, Name, Scopes, CreatedAt, LastUsedAt, RevokedAt);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
