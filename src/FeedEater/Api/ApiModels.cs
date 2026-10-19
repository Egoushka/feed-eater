namespace FeedEater.Api;

/// <summary>With <c>Peek</c> the card at <c>Position</c> is read without starting a session or counting the card as shown.</summary>
public sealed record ApiNextBody(int? Position, bool? Peek);

public sealed record ApiActBody(long Session, int Position, string? Act, string? ClientId);

public sealed record ApiUndoBody(long Session, int Position, string? ClientId);

public sealed record ApiEventBody(long Session, int Position, long? ShownAt, long? DwellMs, string? Direction);

public sealed record ApiFeltBody(long Session, int Felt);

public sealed record ApiEndBody(long Session);

public sealed record OkResponse(bool Ok);
