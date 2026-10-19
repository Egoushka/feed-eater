using FeedEater.Storage;

namespace FeedEater.Api;

/// <summary>With <c>Peek</c> the card at <c>Position</c> is read without starting a session or counting the card as shown.</summary>
public sealed record ApiNextBody(int? Position, bool? Peek);

public sealed record ApiActBody(long Session, int Position, string? Act, string? ClientId);

public sealed record ApiUndoBody(long Session, int Position, string? ClientId);

public sealed record ApiEventBody(long Session, int Position, long? ShownAt, long? DwellMs, string? Direction);

public sealed record ApiFeltBody(long Session, int Felt);

public sealed record ApiEndBody(long Session);

public sealed record OkResponse(bool Ok);

public sealed record ItemResponse(
    long Id, string Title, string? Url, string Feed, DateTimeOffset PublishedAt, string? Text, bool FullText,
    string? Summary, string? Why, string? Suggestion, short? Vote, bool Saved, string? Project, string? Kind);

public sealed record AskRequest(string? Question);

/// <summary>A search result as the API shows it: no ranking internals, UTC time with an offset, a safe URL.</summary>
public sealed record ApiHit(long Id, string Title, string? Url, string Feed, DateTimeOffset PublishedAt, string? Summary, string? Project, string? Kind, short? Vote);

/// <summary><c>Number</c> is the [n] marker in the answer that cites this item.</summary>
public sealed record AskCitation(int Number, ApiHit Hit);

public sealed record AskResponse(string Answer, IReadOnlyList<AskCitation> Citations);

public sealed record ItemAskResponse(string Answer);
