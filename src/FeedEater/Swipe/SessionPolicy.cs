namespace FeedEater.Swipe;

public sealed record SessionRow
{
    public long Id { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public string? EndReason { get; init; }
    public int CardCap { get; init; }
    public int TimeCapS { get; init; }
    public int CardsShown { get; init; }
    public long[] QueueItems { get; init; } = [];
    public string[] QueueCategories { get; init; } = [];
    public short? Felt { get; init; }
    public DateTime? FeltAt { get; init; }
}

public enum Gate
{
    /// <summary>Hand out cards from the open session.</summary>
    Resume,

    /// <summary>The open session ran out of time: end it, then show its summary.</summary>
    Expire,

    /// <summary>A session ended moments ago and was not rated: show its summary again (a reload during the summary).</summary>
    Summary,

    /// <summary>Build a queue and start a session.</summary>
    Start,

    /// <summary>The cooldown or the daily limit holds.</summary>
    Closed,
}

public sealed record Decision(Gate Gate, DateTimeOffset? Until = null, string? Why = null);

/// <summary>
/// The cap, decided on the server from stored state alone: a reload or a second tab cannot restart a clock. Pure, so the rules are tested without a database.
/// </summary>
public static class SessionPolicy
{
    /// <summary>How long after a session ended its summary can still be shown again.</summary>
    public static readonly TimeSpan SummaryWindow = TimeSpan.FromMinutes(10);

    public static Decision Decide(SwipeOptions o, DateTimeOffset now, TimeZoneInfo zone, SessionRow? latest, int startedToday)
    {
        if (latest is { EndedAt: null })
        {
            return now - Utc(latest.StartedAt) >= TimeSpan.FromSeconds(latest.TimeCapS) ? new Decision(Gate.Expire) : new Decision(Gate.Resume);
        }

        if (latest is { EndedAt: { } ended })
        {
            var endedAt = Utc(ended);
            if (latest.FeltAt is null && now - endedAt < SummaryWindow)
            {
                return new Decision(Gate.Summary);
            }

            var reopens = endedAt + TimeSpan.FromMinutes(o.CooldownMinutes);
            if (now < reopens)
            {
                return new Decision(Gate.Closed, reopens, "cooldown");
            }
        }

        if (startedToday >= o.SessionsPerDay)
        {
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var midnight = local.Date.AddDays(1);
            return new Decision(Gate.Closed, new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)), "daily");
        }

        return new Decision(Gate.Start);
    }

    /// <summary>When an expired session is deemed to have ended: at its time cap, not at the moment someone happened to open the page.</summary>
    public static DateTimeOffset EndOfExpired(SessionRow s) => Utc(s.StartedAt) + TimeSpan.FromSeconds(s.TimeCapS);

    public static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));
}
