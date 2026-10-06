using System.Globalization;

namespace FeedEater.Telegram;

/// <summary>Button payloads: <c>v:{id}:u</c>, <c>v:{id}:d</c>, <c>i:{id}</c>, <c>s:{id}</c>, <c>f:{item id}</c>, <c>u:{follow id}</c>, <c>d:{duel}:a|b|s</c>, <c>n</c>. Telegram allows 64 bytes.</summary>
public abstract record CallbackData
{
    public const string Noop = "n";

    public static string Vote(long itemId, short value) => string.Create(CultureInfo.InvariantCulture, $"v:{itemId}:{(value > 0 ? "u" : "d")}");

    public static string Idea(long itemId) => string.Create(CultureInfo.InvariantCulture, $"i:{itemId}");

    public static string Save(long itemId) => string.Create(CultureInfo.InvariantCulture, $"s:{itemId}");

    /// <summary>A duel answer: <paramref name="pick"/> is 'a' (first), 'b' (second) or 's' (skip).</summary>
    public static string Duel(long duelId, char pick) => string.Create(CultureInfo.InvariantCulture, $"d:{duelId}:{pick}");

    public static string Follow(long itemId) => string.Create(CultureInfo.InvariantCulture, $"f:{itemId}");

    public static string Unfollow(long followId) => string.Create(CultureInfo.InvariantCulture, $"u:{followId}");

    public static CallbackData? Parse(string? data) => data?.Split(':') switch
    {
        ["v", var id, "u"] when Id(id) is { } i => new VoteCallback(i, 1),
        ["v", var id, "d"] when Id(id) is { } i => new VoteCallback(i, -1),
        ["i", var id] when Id(id) is { } i => new IdeaCallback(i),
        ["s", var id] when Id(id) is { } i => new SaveCallback(i),
        ["d", var id, "a"] when Id(id) is { } i => new DuelCallback(i, 'a'),
        ["d", var id, "b"] when Id(id) is { } i => new DuelCallback(i, 'b'),
        ["d", var id, "s"] when Id(id) is { } i => new DuelCallback(i, 's'),
        ["f", var id] when Id(id) is { } i => new FollowCallback(i),
        ["u", var id] when Id(id) is { } i => new UnfollowCallback(i),
        [Noop] => new NoopCallback(),
        _ => null,
    };

    /// <summary>The item a button belongs to, or null for a button that names none.</summary>
    public static long? ItemIdOf(string? data) => Parse(data) switch
    {
        VoteCallback v => v.ItemId,
        IdeaCallback i => i.ItemId,
        SaveCallback s => s.ItemId,
        FollowCallback f => f.ItemId,
        _ => null,
    };

    /// <summary>The follow a stop button belongs to, or null for a button that names none.</summary>
    public static long? FollowIdOf(string? data) => Parse(data) is UnfollowCallback u ? u.FollowId : null;

    private static long? Id(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

public sealed record VoteCallback(long ItemId, short Value) : CallbackData;

public sealed record IdeaCallback(long ItemId) : CallbackData;

public sealed record SaveCallback(long ItemId) : CallbackData;

public sealed record DuelCallback(long DuelId, char Pick) : CallbackData;
public sealed record FollowCallback(long ItemId) : CallbackData;

public sealed record UnfollowCallback(long FollowId) : CallbackData;

public sealed record NoopCallback : CallbackData;
