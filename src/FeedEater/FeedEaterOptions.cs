using System.ComponentModel;
using FeedEater.Setup;
using FeedEater.Sources;

namespace FeedEater;

/// <summary>
/// Every setting. Each one carries a <see cref="DescriptionAttribute"/>: docs/CONFIG.md is generated from it and a test fails when
/// the file or a description is missing. An integration is off until its required setting is filled (its <c>Enabled</c> property).
/// </summary>
public sealed class FeedEaterOptions
{
    public const string Section = "FeedEater";

    [Description("IANA time zone for the digest time, quiet hours and the dates shown in the UI and Telegram, e.g. Europe/Berlin.")]
    public string TimeZone { get; set; } = "UTC";

    /// <summary>False in tests that boot the host, so nothing polls real services.</summary>
    [Description("False stops every background job (ingest, digest, polling); the web UI and /mcp still run.")]
    public bool RunJobs { get; set; } = true;

    [Description("Your profile: who you are, your projects and topics. A missing file means the built-in example interests are used and the digest says so.")]
    public string ProfilePath { get; set; } = "/config/profile.json";

    [Description("Local time of day the scheduled digest starts.")]
    public TimeSpan DigestAt { get; set; } = new(7, 30, 0);

    [Description("Local time of day after which a digest that has not gone out is given up for the day.")]
    public TimeSpan DigestGiveUpAt { get; set; } = new(12, 0, 0);

    public SourceOptions Source { get; set; } = new();
    public MinifluxOptions Miniflux { get; set; } = new();
    public LlmOptions Llm { get; set; } = new();
    public CapsOptions Caps { get; set; } = new();
    public WeightsOptions Weights { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public IdeasOptions Ideas { get; set; } = new();
    public PlaneOptions Plane { get; set; } = new();
    public KarakeepOptions Karakeep { get; set; } = new();
    public GitHubOptions GitHub { get; set; } = new();
    public HindsightOptions Hindsight { get; set; } = new();
    public ClusterOptions Cluster { get; set; } = new();
    public FetchOptions Fetch { get; set; } = new();
    public WatchOptions Watch { get; set; } = new();
    public TasteOptions Taste { get; set; } = new();
    public QuietOptions Quiet { get; set; } = new();

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>Where 💡 ideas go: the configured sink, else Plane when it is configured, else the local list.</summary>
    public string IdeaSink => Ideas.Sink is IdeasOptions.PlaneSink or IdeasOptions.LocalSink ? Ideas.Sink : Plane.Enabled ? IdeasOptions.PlaneSink : IdeasOptions.LocalSink;
}

public sealed class IdeasOptions
{
    public const string PlaneSink = "plane";
    public const string LocalSink = "local";

    [Description("Where 💡 ideas go: plane, local, or auto (plane when Plane is configured, else local). Local keeps them on /ui/ideas and in the feed_ideas MCP tool.")]
    public string Sink { get; set; } = "auto";
}

public sealed class ClusterOptions
{
    /// <summary>
    /// Cosine similarity (title and start of the text, text-embedding-3-small) at or above which two items from different feeds
    /// count as one story. Measured on the real archive (2026-10-06): same stories scored 0.80 to 0.86, related but different
    /// ones stayed under 0.80. 0.84 merges only the clear cases. Raise it if unrelated items merge, lower it (0.82) if the same
    /// story shows up twice in a digest.
    /// </summary>
    [Description("Cosine similarity at or above which items from different feeds count as one story. Raise it if unrelated items merge, lower it (0.82) if one story shows up twice.")]
    public double Threshold { get; set; } = 0.84;

    /// <summary>Items published within this many days of each other can cluster.</summary>
    [Description("Items published within this many days of each other can join one story.")]
    public int WindowDays { get; set; } = 3;
}

public sealed class FetchOptions
{
    /// <summary>Off when <see cref="MaxPerDay"/> is 0.</summary>
    public bool Enabled => MaxPerDay > 0;

    /// <summary>Pages fetched per ingest poll, newest items first.</summary>
    [Description("Linked pages fetched per ingest poll, newest items first.")]
    public int MaxPerPoll { get; set; } = 40;

    /// <summary>Fetches per UTC day across the whole service.</summary>
    [Description("Page fetches per UTC day across the whole service; 0 turns linked-page fetching off.")]
    public int MaxPerDay { get; set; } = 400;

    /// <summary>Only items whose own text is shorter than this get their linked page fetched.</summary>
    [Description("Only items whose own text is shorter than this many characters get their linked page fetched.")]
    public int ShortChars { get; set; } = 800;

    /// <summary>Characters of readable page text kept per item.</summary>
    [Description("Characters of readable page text kept per item.")]
    public int PageChars { get; set; } = 6000;

    [Description("Top Hacker News comments added to an HN item.")]
    public int Comments { get; set; } = 5;

    [Description("Base URL of the Hacker News Algolia API.")]
    public string HnApiBase { get; set; } = "https://hn.algolia.com/api/v1/";

    /// <summary>Hosts never fetched (login-walled or hostile); subdomains match.</summary>
    [Description("Hosts never fetched (login-walled or hostile); subdomains match.")]
    public string[] BlockedHosts { get; set; } =
    [
        "facebook.com", "instagram.com", "x.com", "twitter.com", "linkedin.com", "tiktok.com", "youtube.com", "youtu.be",
        "t.me", "discord.com", "medium.com", "nytimes.com", "wsj.com", "bloomberg.com", "ft.com",
    ];
}

public sealed class WatchOptions
{
    /// <summary>Off until a source or a fallback list is set.</summary>
    public bool Enabled => Source.Length > 0 || FallbackPath.Length > 0;

    /// <summary>PINS.md as a file path or an https URL (for a private GitHub repo: the contents API URL plus <see cref="Token"/>). Empty uses the static list.</summary>
    [Description("Release watch: what you run, as a PINS.md file path or an https URL (for a private GitHub repo, the contents API URL plus Watch:Token). Empty uses FallbackPath.")]
    public string Source { get; set; } = "";

    /// <summary>Bearer token for <see cref="Source"/>; leave empty for a public URL.</summary>
    [Secret]
    [Description("Bearer token for Watch:Source; empty for a public URL.")]
    public string Token { get; set; } = "";

    /// <summary>Static list used when Source is empty or fails; empty means no list.</summary>
    [Description("JSON list of what you run (see config/watch.example.json), used when Source is empty or fails. Release watch is off while both are empty.")]
    public string FallbackPath { get; set; } = "";

    /// <summary>Image to upstream GitHub repo map; empty means config/watch-map.json beside the app.</summary>
    [Description("JSON map from container image to upstream GitHub repo; empty uses config/watch-map.json beside the app.")]
    public string MapPath { get; set; } = "";
}

public sealed class QuietOptions
{
    /// <summary>Local time (in <c>FeedEater:TimeZone</c>) at which quiet hours start; both From and To must be set. A window may cross midnight.</summary>
    [Description("Local time at which quiet hours start; both From and To must be set. A window may cross midnight. Empty means no quiet hours.")]
    public TimeSpan? From { get; set; }

    [Description("Local time at which quiet hours end.")]
    public TimeSpan? To { get; set; }

    /// <summary>How long a manual "quiet on" lasts.</summary>
    [Description("Hours a manual /quiet on lasts.")]
    public int ManualHours { get; set; } = 12;
}

public sealed class TasteOptions
{
    /// <summary>
    /// Adds a learned (logistic regression on embeddings) term to the ranking. Off by default; with fewer than <see cref="MinVotes"/>
    /// votes it refuses to switch on and the digest header says so. Run <c>dotnet FeedEater.dll taste</c> first.
    /// </summary>
    [Description("Adds a learned term to the ranking. With fewer than MinVotes votes it refuses to switch on. /learn on|off overrides this.")]
    public bool Learn { get; set; }

    [Description("Votes needed (and 10 of each kind) before the learned term can be switched on.")]
    public int MinVotes { get; set; } = 100;
}

public sealed class MinifluxOptions
{
    /// <summary>On when the server and its API key are set.</summary>
    public bool Enabled => BaseUrl.Length > 0 && Token.Length > 0;

    [Description("Miniflux server URL, e.g. http://miniflux:8080/. Empty means no Miniflux.")]
    public string BaseUrl { get; set; } = "";

    [Secret]
    [Description("Miniflux API key; only read from.")]
    public string Token { get; set; } = "";

    [Description("How often new entries are copied.")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(30);

    [Description("Entries per Miniflux request.")]
    public int PageSize { get; set; } = 100;
}

public sealed class LlmOptions
{
    [Description("OpenAI-compatible endpoint, including the version path (/v1/). Chat completions and embeddings are called under it.")]
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";

    [Secret]
    [Description("API key for the endpoint.")]
    public string ApiKey { get; set; } = "";

    [Description("Embedding model. The database column is fixed at 1536 dimensions.")]
    public string EmbedModel { get; set; } = "text-embedding-3-small";

    [Description("Small model: the one-line verdict on each candidate, and the intent of a reply.")]
    public string TriageModel { get; set; } = "gpt-4.1-nano";

    [Description("Stronger model: reads the top items in full, answers questions about the archive.")]
    public string ReadModel { get; set; } = "gpt-4.1-mini";

    [Description("Texts embedded per request.")]
    public int EmbedBatch { get; set; } = 64;

    /// <summary>USD per month shown against the spend on /ui/usage; 0 shows no budget.</summary>
    [Description("USD per month shown against the spend on /ui/usage; 0 shows no budget.")]
    public decimal MonthlyBudget { get; set; }

    /// <summary>Per model, used when the gateway sends no cost header. A model with no entry and no header has an unknown cost.</summary>
    [Description("Prices per model name in USD per million tokens (Input and Output), e.g. Prices:gpt-4.1-nano:Input. Used when the gateway sends no x-litellm-response-cost header; without either, a call's cost is unknown. Set 0 for a free local model.")]
    public Dictionary<string, ModelPrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cost of one call from <see cref="Prices"/>; null when the model has no entry.</summary>
    public decimal? Estimate(string model, int inputTokens, int outputTokens) =>
        Prices.TryGetValue(model, out var p) ? (inputTokens * p.Input + outputTokens * p.Output) / 1_000_000m : null;
}

public sealed class ModelPrice
{
    [Description("Price of one model (<name> is the model name sent to the API): USD per million input tokens. Used only when the gateway sends no x-litellm-response-cost header; with neither, a call's cost is unknown. 0 marks a free local model.")]
    public decimal Input { get; set; }

    [Description("USD per million output tokens, for the same model.")]
    public decimal Output { get; set; }
}

public sealed class CapsOptions
{
    [Description("Candidates the small model triages per digest.")]
    public int Triage { get; set; } = 60;

    [Description("Items the stronger model reads in full per digest.")]
    public int Read { get; set; } = 12;

    [Description("Days back an item can still be a digest candidate.")]
    public int CandidateDays { get; set; } = 3;

    [Description("Lowest triage relevance (0 to 3) that can reach the digest.")]
    public int MinRelevance { get; set; } = 2;

    [Description("Items with less text than this many characters get their full text fetched before reading.")]
    public int ShortContentChars { get; set; } = 1500;

    [Description("Characters of an item given to triage.")]
    public int TriageChars { get; set; } = 2000;

    [Description("Characters of an item given to the read.")]
    public int ReadChars { get; set; } = 24000;

    [Description("Characters of an item embedded.")]
    public int EmbedChars { get; set; } = 8000;

    [Description("Most recent liked and disliked items used for the taste centroids.")]
    public int Centroid { get; set; } = 500;
}

public sealed class WeightsOptions
{
    [Description("Weight of the similarity to what was liked and disliked.")]
    public double Taste { get; set; } = 0.5;

    [Description("Weight of a feed's 👍 rate.")]
    public double Prior { get; set; } = 0.2;

    [Description("Liked items needed before the taste term counts.")]
    public int MinPositives { get; set; } = 10;

    [Description("Votes a feed needs before its 👍 rate counts.")]
    public int MinFeedVotes { get; set; } = 5;

    /// <summary>Weight of the learned term when <c>Taste:Learn</c> is on: (probability - 0.5) times this.</summary>
    [Description("Weight of the learned term when Taste:Learn is on: (probability - 0.5) times this.")]
    public double Learned { get; set; } = 0.3;
}

public sealed class TelegramOptions
{
    [Description("Telegram Bot API URL.")]
    public string BaseUrl { get; set; } = "https://api.telegram.org/";

    [Secret]
    [Description("Bot token from @BotFather. Without it the digest and the Telegram poller are off.")]
    public string Token { get; set; } = "";

    /// <summary>The owner's Telegram user id: the only sender whose button presses count, and the chat digests go to.</summary>
    [Description("Your numeric Telegram user id: the only sender the bot answers, and the chat digests go to.")]
    public long AllowedUserId { get; set; }
}

public sealed class PlaneOptions
{
    /// <summary>On when the server, its API key and the workspace are set.</summary>
    public bool Enabled => BaseUrl.Length > 0 && Token.Length > 0 && Workspace.Length > 0;

    [Description("Plane server URL. Empty means no Plane.")]
    public string BaseUrl { get; set; } = "";

    [Secret]
    [Description("Plane API key.")]
    public string Token { get; set; } = "";

    [Description("Plane workspace slug.")]
    public string Workspace { get; set; } = "";

    [Description("Plane project identifier for ideas whose item matches no project of its own.")]
    public string FallbackProject { get; set; } = "";
}

public sealed class KarakeepOptions
{
    /// <summary>On when the server and its API key are set.</summary>
    public bool Enabled => BaseUrl.Length > 0 && Token.Length > 0;

    [Description("Karakeep server URL. Empty means no Karakeep: no 📌 button, no bookmark import.")]
    public string BaseUrl { get; set; } = "";

    [Secret]
    [Description("Karakeep API key.")]
    public string Token { get; set; } = "";
}

public sealed class GitHubOptions
{
    /// <summary>Off without a user: no stars import.</summary>
    public bool Enabled => User.Length > 0;

    [Description("GitHub API URL.")]
    public string BaseUrl { get; set; } = "https://api.github.com/";

    [Description("GitHub user whose public stars count as liked items. Empty means no stars import.")]
    public string User { get; set; } = "";
}

public sealed class HindsightOptions
{
    /// <summary>On when the server is set.</summary>
    public bool Enabled => BaseUrl.Length > 0;

    [Description("Hindsight server URL for the weekly reading summary. Empty means no Hindsight.")]
    public string BaseUrl { get; set; } = "";

    [Secret]
    [Description("Hindsight API key; empty when the server needs none.")]
    public string Token { get; set; } = "";

    [Description("Hindsight bank the weekly summary goes to.")]
    public string Bank { get; set; } = "feed-eater";
}
