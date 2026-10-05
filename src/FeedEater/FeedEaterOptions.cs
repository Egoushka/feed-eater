namespace FeedEater;

public sealed class FeedEaterOptions
{
    public const string Section = "FeedEater";

    public string TimeZone { get; set; } = "Europe/Kyiv";

    /// <summary>False in tests that boot the host, so nothing polls real services.</summary>
    public bool RunJobs { get; set; } = true;

    public string ProfilePath { get; set; } = "/config/profile.json";
    public TimeSpan DigestAt { get; set; } = new(7, 30, 0);
    public TimeSpan DigestGiveUpAt { get; set; } = new(12, 0, 0);

    public MinifluxOptions Miniflux { get; set; } = new();
    public LlmOptions Llm { get; set; } = new();
    public CapsOptions Caps { get; set; } = new();
    public WeightsOptions Weights { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public PlaneOptions Plane { get; set; } = new();
    public KarakeepOptions Karakeep { get; set; } = new();
    public GitHubOptions GitHub { get; set; } = new();
    public HindsightOptions Hindsight { get; set; } = new();
    public ClusterOptions Cluster { get; set; } = new();

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

public sealed class ClusterOptions
{
    /// <summary>
    /// Cosine similarity (title and start of the text, text-embedding-3-small) at or above which two items from different feeds
    /// count as one story. Measured on the real archive (2026-10-06): same stories scored 0.80 to 0.86, related but different
    /// ones stayed under 0.80. 0.84 merges only the clear cases. Raise it if unrelated items merge, lower it (0.82) if the same
    /// story shows up twice in a digest.
    /// </summary>
    public double Threshold { get; set; } = 0.84;

    /// <summary>Items published within this many days of each other can cluster.</summary>
    public int WindowDays { get; set; } = 3;
}

public sealed class MinifluxOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:8092/";
    public string Token { get; set; } = "";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(30);
    public int PageSize { get; set; } = 100;
}

public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:4000/";
    public string ApiKey { get; set; } = "";
    public string EmbedModel { get; set; } = "text-embedding-3-small";
    public string TriageModel { get; set; } = "gpt-4.1-nano";
    public string ReadModel { get; set; } = "claude-haiku-4-5";
    public int EmbedBatch { get; set; } = 64;

    /// <summary>USD per month shown against the spend on /ui/usage; 0 shows no budget.</summary>
    public decimal MonthlyBudget { get; set; }
}

public sealed class CapsOptions
{
    public int Triage { get; set; } = 60;
    public int Read { get; set; } = 12;
    public int CandidateDays { get; set; } = 3;
    public int MinRelevance { get; set; } = 2;
    public int ShortContentChars { get; set; } = 1500;
    public int TriageChars { get; set; } = 2000;
    public int ReadChars { get; set; } = 24000;
    public int EmbedChars { get; set; } = 8000;
    public int Centroid { get; set; } = 500;
}

public sealed class WeightsOptions
{
    public double Taste { get; set; } = 0.5;
    public double Prior { get; set; } = 0.2;
    public int MinPositives { get; set; } = 10;
    public int MinFeedVotes { get; set; } = 5;
}

public sealed class TelegramOptions
{
    public string BaseUrl { get; set; } = "https://api.telegram.org/";
    public string Token { get; set; } = "";

    /// <summary>Yehor's Telegram user id: the only sender whose button presses count, and the chat digests go to.</summary>
    public long AllowedUserId { get; set; }
}

public sealed class PlaneOptions
{
    public string BaseUrl { get; set; } = "http://plane-proxy/";
    public string Token { get; set; } = "";
    public string Workspace { get; set; } = "homelab";
    public string FallbackProject { get; set; } = "FEED";
}

public sealed class KarakeepOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:3009/";
    public string Token { get; set; } = "";
}

public sealed class GitHubOptions
{
    public string BaseUrl { get; set; } = "https://api.github.com/";
    public string User { get; set; } = "Egoushka";
}

public sealed class HindsightOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:8888/";
    public string Token { get; set; } = "";
    public string Bank { get; set; } = "learning";
}
