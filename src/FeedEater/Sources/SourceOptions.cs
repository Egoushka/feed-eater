using System.ComponentModel;

namespace FeedEater.Sources;

public sealed class SourceOptions
{
    public const string Builtin = "builtin";
    public const string Miniflux = "miniflux";

    /// <summary><c>builtin</c> (default) reads feeds itself; <c>miniflux</c> copies entries from Miniflux. Switching on a populated database is unsupported.</summary>
    [Description("Where entries come from: builtin (feed-eater reads the feeds itself) or miniflux (copies entries from Miniflux). Switching on a populated database is unsupported.")]
    public string Kind { get; set; } = Builtin;

    public bool IsBuiltin => !string.Equals(Kind, Miniflux, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <see cref="Kind"/> is not usable, or null. Anything but a known kind would silently run the empty built-in reader, so startup refuses it and doctor reports it.</summary>
    public string? KindProblem => string.Equals(Kind, Builtin, StringComparison.OrdinalIgnoreCase) || string.Equals(Kind, Miniflux, StringComparison.OrdinalIgnoreCase)
        ? null
        : $"Source:Kind is \"{Kind}\"; it must be builtin or miniflux";

    /// <summary>Hosts a configured feed URL may name although they are private (self-hosted RSSHub, Nitter); any port. Exact host names. Article and linked-page fetches never get this.</summary>
    [Description("Exact host names a configured feed URL may point at although they are private (self-hosted RSSHub, Nitter); any port. Applies to feed URLs only, never to article or linked-page fetches.")]
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>The first fetch of a feed keeps only entries published this many days back, so a new instance does not embed whole histories.</summary>
    [Description("The first fetch of a feed keeps only entries published this many days back, so a new instance does not embed whole histories.")]
    public int BackfillDays { get; set; } = 14;

    /// <summary>How often each feed is fetched; repeated failures double the wait up to <see cref="MaxBackoff"/>.</summary>
    [Description("How often each feed is fetched; repeated failures double the wait up to MaxBackoff.")]
    public TimeSpan FeedInterval { get; set; } = TimeSpan.FromMinutes(30);

    [Description("The longest wait between fetches of a feed that keeps failing.")]
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the ingest loop looks for feeds that are due.</summary>
    [Description("How often the ingest loop looks for feeds that are due.")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}
