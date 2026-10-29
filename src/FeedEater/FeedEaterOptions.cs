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
    public WeeklyIdeasOptions WeeklyIdeas { get; set; } = new();
    public KarakeepOptions Karakeep { get; set; } = new();
    public LibraryOptions Library { get; set; } = new();
    public GitHubOptions GitHub { get; set; } = new();
    public HindsightOptions Hindsight { get; set; } = new();
    public ClusterOptions Cluster { get; set; } = new();
    public FetchOptions Fetch { get; set; } = new();
    public WatchOptions Watch { get; set; } = new();
    public TasteOptions Taste { get; set; } = new();
    public QuietOptions Quiet { get; set; } = new();
    public DuelOptions Duel { get; set; } = new();
    public FollowOptions Follow { get; set; } = new();
    public SwipeOptions Swipe { get; set; } = new();
    public ApiOptions Api { get; set; } = new();
    public PriorArtOptions PriorArt { get; set; } = new();

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>Where 💡 ideas go: the configured sink, else GitHub issues when they are configured, else the local list.</summary>
    public string IdeaSink => Ideas.Sink is IdeasOptions.GitHubSink or IdeasOptions.LocalSink ? Ideas.Sink : GitHub.IssuesEnabled ? IdeasOptions.GitHubSink : IdeasOptions.LocalSink;
}

public sealed class IdeasOptions
{
    public const string GitHubSink = "github";
    public const string LocalSink = "local";

    [Description("Where 💡 ideas go: github, local, or auto (github when GitHub issues are configured, else local). GitHub opens an issue labelled triage in the project's repo; local keeps them on /ui/ideas and in the feed_ideas MCP tool.")]
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

public sealed class DuelOptions
{
    [Description("False turns the twice-daily duel off: two unvoted items in one message, one tap picks the one you would rather read. Needs the Telegram bot.")]
    public bool Enabled { get; set; } = true;

    /// <summary>A string, not a list: the configuration binder appends to a list default instead of replacing it.</summary>
    [Description("Local times a duel is sent, comma separated (HH:mm). A slot missed while the service was down is sent only within 2 hours of its time.")]
    public string Times { get; set; } = "12:30,20:30";

    [Description("Most duels sent per local day; later slots of the day are skipped.")]
    public int PerDay { get; set; } = 2;
}

public sealed class FollowOptions
{
    [Description("Follow this story: a 🧵 button on items that sends later items on the same story as replies to one message. False removes the button and the job.")]
    public bool Enabled { get; set; } = true;

    [Description("Days a story is followed before it is closed with a summary.")]
    public int Days { get; set; } = 14;

    [Description("Stories followed at once.")]
    public int MaxActive { get; set; } = 5;

    [Description("Messages sent per followed story; the follow closes when it is reached.")]
    public int MaxMessages { get; set; } = 50;
}

public sealed class ApiOptions
{
    [Description("The address the phone app uses; the pairing QR carries it. Empty: the address the setup page was opened on.")]
    public string BaseUrl { get; set; } = "";
}

public sealed class SwipeOptions
{
    [Description("Swipe mode: a phone page at /swipe showing one card per screen, with a hard cap per session. Off by default; needs Mcp:Token for the sign-in.")]
    public bool Enabled { get; set; }

    [Description("Cards in one session; the summary card follows the last one.")]
    public int Cards { get; set; } = 40;

    [Description("Minutes after which a session ends with the summary card, whatever the card count.")]
    public int Minutes { get; set; } = 15;

    [Description("Minutes after a session ends before the next one can start.")]
    public int CooldownMinutes { get; set; } = 180;

    [Description("Most sessions that can start per local day.")]
    public int SessionsPerDay { get; set; } = 3;

    [Description("Items published in the last this many days can be shown.")]
    public int CandidateDays { get; set; } = 14;

    [Description("An item shown in swipe mode is not shown again for this many days.")]
    public int RepeatDays { get; set; } = 30;

    [Description("Your categories (what counts as a good use of attention: shares, events, seed flag); shape in categories.example.json. A missing file means one category, everything.")]
    public string CategoriesPath { get; set; } = "/config/categories.json";

    [Description("Cosine similarity below which an item belongs to no category.")]
    public double CategoryFloor { get; set; } = 0.2;

    [Description("Share of cards chosen for exploration: a random item from outside the best-ranked part that is unlike the rest of the session. Its yield is the only honest test of the ranking.")]
    public double Epsilon { get; set; } = 0.2;

    [Description("Good and not-good examples (each) the yield model needs before it adds to the ranking.")]
    public int YieldMinEvents { get; set; } = 30;

    [Description("Weight of the yield model's (probability - 0.5) next to the taste score, once it has enough examples.")]
    public double Yield { get; set; } = 1.0;

    public SwipeRecallOptions Recall { get; set; } = new();
    public SwipeSeedOptions Seeds { get; set; } = new();
    public SwipeNotesOptions Notes { get; set; } = new();

    public TimeSpan Cap => TimeSpan.FromMinutes(Minutes);
}

public sealed class SwipeRecallOptions
{
    [Description("Recall cards: questions made from the items you liked or saved (and from your notes) come back at growing intervals. False turns generating and showing them off.")]
    public bool Enabled { get; set; } = true;

    [Description("Most recall cards in one session; due cards come first, so a backlog never takes the whole session.")]
    public int PerSession { get; set; } = 12;

    [Description("Most liked or saved items turned into questions per local day (one small-model call each).")]
    public int PerDay { get; set; } = 10;
}

public sealed class SwipeSeedOptions
{
    [Description("Post seeds: a one-line angle for a post, drafted from an item you liked or saved, with a send button that files it as an idea. False turns generating and showing them off.")]
    public bool Enabled { get; set; } = true;

    [Description("Most seeds in one session.")]
    public int PerSession { get; set; } = 2;

    [Description("Most seeds drafted per local day (one small-model call each).")]
    public int PerDay { get; set; } = 3;
}

public sealed class SwipeNotesOptions
{
    public bool Enabled => BaseUrl.Length > 0 && Token.Length > 0;

    [Description("Server that lists your notes at GET /api/v1/notes (items with id, topic, points, createdAt), for recall cards. Empty means no notes.")]
    public string BaseUrl { get; set; } = "";

    [Secret]
    [Description("Bearer token for the notes server.")]
    public string Token { get; set; } = "";
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

    [Description("Sent as the dimensions parameter of embedding requests; 0 leaves it out. Models that can shorten their output (text-embedding-3-large) need 1536 here.")]
    public int EmbedDimensions { get; set; }

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
    public int Triage { get; set; } = 400;

    [Description("Items the stronger model reads in full per digest.")]
    public int Read { get; set; } = 12;

    [Description("Days back an item can still be a digest candidate.")]
    public int CandidateDays { get; set; } = 3;

    [Description("Lowest triage relevance (0 to 3) that can reach the digest.")]
    public int MinRelevance { get; set; } = 2;

    [Description("Lowest relevance (0 to 3) the reader model can give an item after reading it and keep it in the digest. Items below MinRelevance but at or above this go last.")]
    public int MinReadRelevance { get; set; } = 1;

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
    [Description("Bot token from @BotFather. Without it the Telegram poller is off; the digest and the inbox jobs (duels, follows, the weekly review, ideas, the autopsy) still run when Swipe:Enabled is true, and wait in the swipe stack.")]
    public string Token { get; set; } = "";

    /// <summary>The owner's Telegram user id: the only sender whose button presses count, and the chat digests go to.</summary>
    [Description("Your numeric Telegram user id: the only sender the bot answers, and the chat digests go to.")]
    public long AllowedUserId { get; set; }
}

public sealed class WeeklyIdeasOptions
{
    [Description("Mondays: the week's improve items and what you liked or saved, ranked into a shortlist, with the top few on Telegram. Ideas are filed as GitHub issues labelled triage only after Keep (Telegram button, swipe card). Needs GitHub issues (GitHub:Token and GitHub:FallbackRepo), and Telegram or Swipe. False turns the job off.")]
    public bool Enabled { get; set; } = true;

    [Description("Local time of day on Monday the shortlist runs. A run missed while the service was down happens at start.")]
    public TimeSpan At { get; set; } = new(9, 0, 0);

    [Description("Most ideas filed as GitHub issues per week.")]
    public int MaxFiled { get; set; } = 5;

    [Description("Ideas listed in the Telegram message, best first.")]
    public int TopInMessage { get; set; } = 3;

    [Description("GitHub contents API URL of the backlog file, to keep the shortlist from repeating a row. Empty means the backlog is not checked; GitHub issues and earlier shortlists still are.")]
    public string BacklogUrl { get; set; } = "";

    [Secret]
    [Description("GitHub token that can read the backlog file; empty for a public repository.")]
    public string BacklogToken { get; set; } = "";
}

public sealed class KarakeepOptions
{
    /// <summary>On when the server and its API key are set.</summary>
    public bool Enabled => BaseUrl.Length > 0 && Token.Length > 0;

    [Description("Karakeep server URL. Empty means no Karakeep: no mirror of saves, no bookmark import.")]
    public string BaseUrl { get; set; } = "";

    [Secret]
    [Description("Karakeep API key.")]
    public string Token { get; set; } = "";

    [Description("Copy each new save to Karakeep as a bookmark. The library works without it.")]
    public bool Mirror { get; set; }
}

public sealed class LibraryOptions
{
    [Description("Saved items the background enrichment (full text, read, embedding) handles per pass.")]
    public int EnrichBatch { get; set; } = 5;

    [Description("How long the enrichment loop waits after a pass that found nothing to do. After a pass that did work it waits 30 seconds.")]
    public TimeSpan EnrichIdle { get; set; } = TimeSpan.FromMinutes(5);

    [Description("A saved item still unread after this many days is a candidate for the weekly resurface.")]
    public int ResurfaceDays { get; set; } = 14;

    [Description("At most this many saved items are resurfaced in one week.")]
    public int ResurfacePerWeek { get; set; } = 3;
}

public sealed class GitHubOptions
{
    /// <summary>Off without a user: no stars import.</summary>
    public bool Enabled => User.Length > 0;

    /// <summary>On when the token and the fallback repo are set: ideas become issues.</summary>
    public bool IssuesEnabled => Token.Length > 0 && FallbackRepo.Length > 0;

    [Description("GitHub API URL.")]
    public string BaseUrl { get; set; } = "https://api.github.com/";

    [Description("GitHub user whose public stars count as liked items. Empty means no stars import.")]
    public string User { get; set; } = "";

    [Secret]
    [Description("GitHub token, sent on every GitHub call. It lifts the 60 requests an hour limit of anonymous calls (the hype autopsy snapshots stop at 40 requests a run without it); a token with no scopes is enough for that. Filing ideas as issues needs one that can write issues in every repo of the profile (classic: repo, or public_repo when all are public).")]
    public string Token { get; set; } = "";

    [Secret]
    [Description("Tokens for owners that GitHub:Token cannot reach (a fine-grained token covers one owner), as owner=token pairs separated by commas, e.g. some-org=ghp_x. A repo of a listed owner is called with that token instead of GitHub:Token.")]
    public string OwnerTokens { get; set; } = "";

    /// <summary>The token for <paramref name="owner"/>: its entry of <see cref="OwnerTokens"/>, else <see cref="Token"/>.</summary>
    public string TokenFor(string owner) => OwnerTokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(pair => pair.Split('=', 2, StringSplitOptions.TrimEntries))
        .FirstOrDefault(p => p.Length == 2 && p[1].Length > 0 && string.Equals(p[0], owner, StringComparison.OrdinalIgnoreCase))?[1] ?? Token;

    [Description("Repo (owner/name) for ideas whose item matches no project with a repo of its own. With GitHub:Token it turns GitHub issues on: ideas are filed as issues labelled triage.")]
    public string FallbackRepo { get; set; } = "";
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

public sealed class PriorArtOptions
{
    [Description("Prior-art search: cosine similarity at or above which a fact counts as relevant to the question. A fact below it is still listed, flagged not relevant, and does not count toward coverage.")]
    public double MinSimilarity { get; set; } = 0.35;

    [Description("Prior-art search: a claim recorded more than this many days ago is flagged stale.")]
    public int StaleAfterDays { get; set; } = 180;

    [Description("Prior-art search: coverage is hit when at least this many relevant facts come from at least PriorArt:HitMinTools tools; some relevant facts but fewer is thin; none is miss.")]
    public int HitMinFacts { get; set; } = 3;

    [Description("Prior-art search: the number of different tools the relevant facts of a hit must come from.")]
    public int HitMinTools { get; set; } = 2;
}
