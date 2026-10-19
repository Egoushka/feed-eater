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

/// <summary>An active follow as the API shows it: no Telegram message id, UTC times.</summary>
public sealed record FollowResponse(long Id, long RootItemId, string Title, string? Url, DateTimeOffset StartedAt, DateTimeOffset EndsAt, int Sent);

public sealed record StartFollowBody(long? ItemId);

public sealed record StartFollowResponse(string Outcome);

public sealed record FeedResponse(
    long Id, string Title, string? Category, string? SiteUrl, string? FeedUrl, bool Muted,
    int Items30d, int Shown30d, int Up30d, int Down30d, double? UpRate, int? FailCount, string? LastError);

public sealed record AddFeedBody(string? Url);

public sealed record AddFeedResponse(string Title, bool Existing, int Items);

public sealed record MuteFeedBody(bool? Muted);

/// <summary>The quiet window and digest time come from config and are read-only here; <c>Quiet</c> and <c>Learn</c> are the writable switches.</summary>
public sealed record SettingsResponse(bool QuietNow, string QuietText, bool QuietWindow, string? QuietFrom, string? QuietTo, bool Learn, string LearnText, string DigestAt);

public sealed record SettingsBody(bool? Quiet, bool? Learn);

public sealed record RunDigestBody(bool? Resend);
