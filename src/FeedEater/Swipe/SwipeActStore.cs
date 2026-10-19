using Dapper;
using FeedEater.Storage;

namespace FeedEater.Swipe;

public sealed record ActRecord(Guid ClientId, long SessionId, int Position, string Act, bool? Ok, string? Message, short? PreviousValue, float? PreviousWeight,
    bool? PreviouslySaved, DateTime At, DateTime? UndoneAt);

/// <summary>Acts the client sent with an id. The insert is the claim: a second call with the same id finds the row and runs nothing.</summary>
public sealed class SwipeActStore(FeedDb db)
{
    private const string Columns = "client_id, session_id, position, act, ok, message, previous_value, previous_weight, previously_saved, at, undone_at";

    public async Task<ActRecord?> ClaimAsync(Guid clientId, long sessionId, int position, string act, (short Value, float Weight)? previous, bool previouslySaved, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var inserted = await c.ExecuteAsync(new CommandDefinition(
            """
            insert into swipe_acts (client_id, session_id, position, act, previous_value, previous_weight, previously_saved)
            values (@clientId, @sessionId, @position, @act, @value, @weight, @previouslySaved) on conflict (client_id) do nothing
            """, new { clientId, sessionId, position, act, value = previous?.Value, weight = previous?.Weight, previouslySaved }, cancellationToken: ct));
        return inserted == 1 ? null : await GetAsync(clientId, ct);
    }

    public async Task CompleteAsync(Guid clientId, bool ok, string message, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update swipe_acts set ok = @ok, message = @message where client_id = @clientId", new { clientId, ok, message }, cancellationToken: ct));
    }

    public async Task<ActRecord?> GetAsync(Guid clientId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ActRecord>(new CommandDefinition(
            $"select {Columns} from swipe_acts where client_id = @clientId", new { clientId }, cancellationToken: ct));
    }

    /// <summary>False when it was undone already.</summary>
    public async Task<bool> MarkUndoneAsync(Guid clientId, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "update swipe_acts set undone_at = @at where client_id = @clientId and undone_at is null", new { clientId, at = at.UtcDateTime }, cancellationToken: ct)) == 1;
    }
}
