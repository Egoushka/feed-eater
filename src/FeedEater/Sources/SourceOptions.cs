namespace FeedEater.Sources;

public sealed class SourceOptions
{
    public const string Builtin = "builtin";
    public const string Miniflux = "miniflux";

    /// <summary><c>builtin</c> (default) reads feeds itself; <c>miniflux</c> copies entries from Miniflux. Switching on a populated database is unsupported.</summary>
    public string Kind { get; set; } = Builtin;

    public bool IsBuiltin => !string.Equals(Kind, Miniflux, StringComparison.OrdinalIgnoreCase);

    /// <summary>Hosts a fetch may reach although they are private (self-hosted RSSHub, Nitter); any port. Exact host names.</summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>The first fetch of a feed keeps only entries published this many days back, so a new instance does not embed whole histories.</summary>
    public int BackfillDays { get; set; } = 14;

    /// <summary>How often each feed is fetched; repeated failures double the wait up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan FeedInterval { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the ingest loop looks for feeds that are due.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}
