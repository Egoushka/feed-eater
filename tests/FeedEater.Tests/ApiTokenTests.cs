using System.Security.Cryptography;
using System.Text;
using Dapper;
using FeedEater.Api;
using Microsoft.Extensions.Time.Testing;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiTokenTests(PostgresFixture pg)
{
    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        return (await c.ExecuteScalarAsync<T>(sql))!;
    }

    [Fact]
    public async Task A_created_token_verifies_and_is_stored_only_as_its_hash()
    {
        await pg.ResetAsync();
        var store = new ApiTokenStore(pg.Db, TimeProvider.System);
        var (id, token) = await store.CreateAsync("phone", [ApiScopes.App], default);

        Assert.StartsWith("fe_", token);
        Assert.True(token.Length >= 40);
        Assert.Equal(id, (await store.VerifyAsync(token, default))!.Id);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), await ScalarAsync<byte[]>("select hash from api_tokens"));
    }

    [Fact]
    public async Task A_revoked_or_unknown_token_does_not_verify()
    {
        await pg.ResetAsync();
        var store = new ApiTokenStore(pg.Db, TimeProvider.System);
        var (id, token) = await store.CreateAsync("phone", [ApiScopes.App], default);
        Assert.True(await store.RevokeAsync(id, default));

        Assert.Null(await store.VerifyAsync(token, default));
        Assert.Null(await store.VerifyAsync("fe_nope", default));
        Assert.False(await store.RevokeAsync(id, default));
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        await pg.ResetAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var store = new ApiTokenStore(pg.Db, time);
        var (_, token) = await store.CreateAsync("phone", [ApiScopes.App], default);

        await store.VerifyAsync(token, default);
        var first = await ScalarAsync<DateTime>("select last_used_at from api_tokens");
        time.Advance(TimeSpan.FromSeconds(30));
        await store.VerifyAsync(token, default);
        Assert.Equal(first, await ScalarAsync<DateTime>("select last_used_at from api_tokens"));
        time.Advance(TimeSpan.FromSeconds(31));
        await store.VerifyAsync(token, default);
        Assert.True(await ScalarAsync<DateTime>("select last_used_at from api_tokens") > first);
    }

    [Fact]
    public async Task A_verified_token_is_remembered_for_ten_minutes_and_forgotten_on_revoke()
    {
        await pg.ResetAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var store = new ApiTokenStore(pg.Db, time);
        var (id, token) = await store.CreateAsync("phone", [ApiScopes.App], default);
        var (_, other) = await store.CreateAsync("tablet", [ApiScopes.App], default);
        Assert.False(store.VerifiedLately(token));

        await store.VerifyAsync(token, default);
        await store.VerifyAsync(other, default);
        Assert.True(store.VerifiedLately(token));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(store.VerifiedLately(token));

        await store.VerifyAsync(token, default);
        await store.VerifyAsync(other, default);
        await store.RevokeAsync(id, default);
        Assert.False(store.VerifiedLately(token));
        Assert.True(store.VerifiedLately(other));
    }

    [Fact]
    public async Task The_list_shows_every_token_with_its_scopes_and_never_the_secret()
    {
        await pg.ResetAsync();
        var store = new ApiTokenStore(pg.Db, TimeProvider.System);
        var (id, _) = await store.CreateAsync("phone", [ApiScopes.App], default);
        await store.RevokeAsync(id, default);

        var row = Assert.Single(await store.ListAsync(default));
        Assert.Equal("phone", row.Name);
        Assert.Equal([ApiScopes.App], row.Scopes);
        Assert.NotNull(row.RevokedAt);
        Assert.Null(row.LastUsedAt);
    }
}
