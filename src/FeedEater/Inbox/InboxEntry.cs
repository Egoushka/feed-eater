using System.Text.Json.Nodes;

namespace FeedEater.Inbox;

public static class InboxKinds
{
    public const string Follow = "follow";
    public const string Duel = "duel";
    public const string Release = "release";
    public const string Idea = "idea";
    public const string Review = "review";
    public const string Autopsy = "autopsy";
    public const string Resurface = "resurface";
    public const string Notice = "notice";
}

/// <summary>What a job has to say. <paramref name="Key"/> is unique per thing said, so a retried job writes one row.</summary>
public sealed record InboxEntry(string Key, string Kind, long? ItemId = null, long? RefId = null, JsonObject? Payload = null, DateTimeOffset? ExpiresAt = null);

/// <summary><paramref name="TelegramMessageId"/> is null when Telegram is off.</summary>
public sealed record InboxPost(long Id, long? TelegramMessageId);

public sealed record InboxRow(long Id, string Key, string Kind, long? ItemId, long? RefId, JsonObject Payload, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, long? ShownSession, DateTimeOffset? DoneAt);
