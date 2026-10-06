using System.Globalization;

namespace FeedEater.Telegram;

/// <summary>Button payloads: <c>v:{id}:u</c>, <c>v:{id}:d</c>, <c>i:{id}</c>, <c>s:{id}</c>, <c>n</c>. Telegram allows 64 bytes.</summary>
public abstract record CallbackData
{
    public const string Noop = "n";

    public static string Vote(long itemId, short value) => string.Create(CultureInfo.InvariantCulture, $"v:{itemId}:{(value > 0 ? "u" : "d")}");

    public static string Idea(long itemId) => string.Create(CultureInfo.InvariantCulture, $"i:{itemId}");

    public static string Save(long itemId) => string.Create(CultureInfo.InvariantCulture, $"s:{itemId}");

    public static CallbackData? Parse(string? data) => data?.Split(':') switch
    {
        ["v", var id, "u"] when Id(id) is { } i => new VoteCallback(i, 1),
        ["v", var id, "d"] when Id(id) is { } i => new VoteCallback(i, -1),
        ["i", var id] when Id(id) is { } i => new IdeaCallback(i),
        ["s", var id] when Id(id) is { } i => new SaveCallback(i),
        [Noop] => new NoopCallback(),
        _ => null,
    };

    private static long? Id(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

public sealed record VoteCallback(long ItemId, short Value) : CallbackData;

public sealed record IdeaCallback(long ItemId) : CallbackData;

public sealed record SaveCallback(long ItemId) : CallbackData;

public sealed record NoopCallback : CallbackData;
