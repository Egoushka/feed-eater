using System.Globalization;
using System.Net;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Memory;
using FeedEater.Plane;
using FeedEater.Profiles;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Watch;

namespace FeedEater.Setup;

/// <summary>Postgres answers, and the <c>vector</c> extension is installed or can be (the first start installs it).</summary>
internal sealed class DatabaseCheck(FeedDb db) : IIntegrationCheck
{
    private sealed record Info(string Version, string? Installed, bool Available);

    public string Name => "database";
    public bool Required => true;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await db.DataSource.OpenConnectionAsync(ct);
            var info = await connection.QuerySingleAsync<Info>(new CommandDefinition(
                """
                select split_part(current_setting('server_version'), ' ', 1) as version,
                       (select extversion from pg_extension where extname = 'vector') as installed,
                       exists (select 1 from pg_available_extensions where name = 'vector') as available
                """, cancellationToken: ct));
            if (info.Installed is not null)
            {
                return CheckResult.Ok($"Postgres {info.Version}, pgvector {info.Installed}");
            }

            return info.Available
                ? CheckResult.Ok($"Postgres {info.Version}; pgvector is available and is installed on the first start")
                : CheckResult.Fail($"Postgres {info.Version} has no vector extension", "Use an image that ships pgvector, such as pgvector/pgvector:pg18; plain postgres does not.");
        }
        catch (Exception ex) when ((ex is NpgsqlException or ArgumentException or InvalidOperationException) && !ct.IsCancellationRequested)
        {
            return CheckResult.Fail(Hints.Describe(ex), Fix(ex));
        }
    }

    private static string Fix(Exception ex) => ex switch
    {
        PostgresException { SqlState: "28P01" or "28000" } => "The user or password is wrong: check ConnectionStrings__FeedEater and the database's own password.",
        PostgresException { SqlState: "3D000" } => "That database does not exist: check Database= in ConnectionStrings__FeedEater.",
        _ => "Check ConnectionStrings__FeedEater (host, database, user, password) and that the database container is up: docker compose ps.",
    };
}

/// <summary>A chat call to each model the digest uses.</summary>
internal sealed class LlmChatCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : IIntegrationCheck
{
    public string Name => "llm chat";
    public bool Required => true;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        var llm = options.Value.Llm;
        var models = new[] { llm.TriageModel, llm.ReadModel }.Distinct().ToList();
        var failures = (await Task.WhenAll(models.Select(m => TryAsync(m, ct)))).Where(f => f is not null).Select(f => f!.Value).ToList();
        if (failures.Count == 0)
        {
            return CheckResult.Ok($"{Hints.Host(llm.BaseUrl)} answered for {string.Join(" and ", models)}");
        }

        var (model, error) = failures[0];
        var key = model == llm.TriageModel ? "Llm:TriageModel" : "Llm:ReadModel";
        var said = failures.GroupBy(f => Hints.Describe(f.Error)).Select(g => $"{string.Join(" and ", g.Select(f => f.Model))}: {g.Key}");
        return CheckResult.Fail(string.Join("; ", said), LlmFix.For(error, llm.BaseUrl, key));
    }

    private async Task<(string Model, Exception Error)?> TryAsync(string model, CancellationToken ct)
    {
        try
        {
            await services.GetRequiredService<LiteLlmClient>().ChatAsync(model, "Reply with one word.", "ok", 5, "doctor", ct);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (model, ex);
        }
    }
}

/// <summary>One embedding, which must have the 1536 dimensions of the <c>vector(1536)</c> column.</summary>
internal sealed class LlmEmbeddingCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : IIntegrationCheck
{
    public const int Dimensions = 1536;

    public string Name => "llm embeddings";
    public bool Required => true;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        var llm = options.Value.Llm;
        try
        {
            var vectors = await services.GetRequiredService<LiteLlmClient>().EmbedAsync(["feed-eater doctor"], "doctor", ct);
            return vectors[0].Length == Dimensions
                ? CheckResult.Ok($"{llm.EmbedModel} returns {Dimensions} dimensions")
                : CheckResult.Fail($"{llm.EmbedModel} returns {vectors[0].Length} dimensions, the database column is vector({Dimensions})",
                    $"The column size is fixed. Set {Hints.Env("Llm:EmbedModel")} to a model that returns {Dimensions} dimensions, such as text-embedding-3-small, or set {Hints.Env("Llm:EmbedDimensions")}={Dimensions} for a model that can shorten its output.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail(Hints.Describe(ex), LlmFix.For(ex, llm.BaseUrl, "Llm:EmbedModel"));
        }
    }
}

internal static class LlmFix
{
    public static string For(Exception ex, string baseUrl, string modelKey) => ex switch
    {
        BudgetExceededException => "Raise the budget on the gateway, or use another key.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadRequest } =>
            $"The model name is probably unknown to {Hints.Host(baseUrl)}: check {Hints.Env(modelKey)}. {Hints.Env("Llm:BaseUrl")} must end in the version path, such as https://api.openai.com/v1/.",
        _ => Hints.Http(ex, "Llm:BaseUrl", "Llm:ApiKey"),
    };
}

/// <summary>The bot token works (<c>getMe</c>) and an owner is set; while it is not, only <c>/start</c> is answered.</summary>
internal sealed class TelegramCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : IIntegrationCheck
{
    public string Name => "telegram";
    public bool Required => true;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        var telegram = options.Value.Telegram;
        if (telegram.Token.Length == 0)
        {
            return CheckResult.Fail("no bot token", $"Create a bot with @BotFather in Telegram and set {Hints.Env("Telegram:Token")} to the token it gives you.");
        }

        string? bot;
        try
        {
            bot = await services.GetRequiredService<TelegramClient>().GetMeAsync(ct);
        }
        catch (TelegramException ex)
        {
            return CheckResult.Fail(ex.Message, $"Telegram refused the token: copy it again from @BotFather (/mybots, then API Token) into {Hints.Env("Telegram:Token")}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail(Hints.Describe(ex), Hints.Http(ex, "Telegram:BaseUrl", "Telegram:Token"));
        }

        if (telegram.AllowedUserId == 0)
        {
            return CheckResult.Fail($"bot @{bot} works, but no owner is set",
                $"Send /start to @{bot} in Telegram; it answers with your numeric id. Set {Hints.Env("Telegram:AllowedUserId")} to it and restart.");
        }

        return CheckResult.Ok($"bot @{bot}, owner id {telegram.AllowedUserId.ToString(CultureInfo.InvariantCulture)}");
    }
}

/// <summary>The profile file is valid, or the built-in example is in use (a warning: it works, but it is not your interests).</summary>
internal sealed class ProfileCheck(IOptions<FeedEaterOptions> options) : IIntegrationCheck
{
    public string Name => "profile";
    public bool Required => false;

    public Task<CheckResult> RunAsync(CancellationToken ct)
    {
        var path = options.Value.ProfilePath;
        try
        {
            var (profile, isExample) = ProfileFile.LoadOrExample(path);
            return Task.FromResult(isExample
                ? CheckResult.Warn($"example interests in use, no file at {path}", $"Copy profile.example.json to {path} and write your own projects and topics.")
                : CheckResult.Ok($"{path}: {profile.Projects.Count} projects, {profile.Topics.Count} topics"));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(CheckResult.Fail($"{path}: {ex.Message}", "Fix the file; the shape is in profile.example.json."));
        }
    }
}

/// <summary>Asks the active <see cref="IFeedSourceProbe"/>: where feeds come from is the one part of setup that differs by mode.</summary>
internal sealed class FeedSourceCheck(IServiceProvider services) : IIntegrationCheck
{
    public string Name => "feed source";
    public bool Required => true;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var probe = services.GetRequiredService<IFeedSourceProbe>();
            var result = await probe.CheckAsync(ct);
            return result with { Detail = $"{probe.Name}: {result.Detail}" };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail(Hints.Describe(ex), "Check the feed source settings and the database.");
        }
    }
}

internal sealed class MinifluxCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : ServiceCheck(services, options)
{
    public override string Name => "miniflux";
    protected override string? OffReason => Settings.Miniflux.Enabled ? null : "not configured";

    protected override async Task<string> ProbeAsync(CancellationToken ct)
    {
        await Services.GetRequiredService<MinifluxClient>().EntriesAfterAsync(0, 1, ct);
        return $"{Hints.Host(Settings.Miniflux.BaseUrl)} answers";
    }

    protected override string Fix(Exception ex) => Hints.Http(ex, "Miniflux:BaseUrl", "Miniflux:Token");
}

internal sealed class PlaneCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : ServiceCheck(services, options)
{
    public override string Name => "plane";
    protected override string? OffReason => Settings.Plane.Enabled ? null : "not configured";

    protected override async Task<string> ProbeAsync(CancellationToken ct)
    {
        var plane = Services.GetRequiredService<PlaneClient>();
        if (Settings.Plane.FallbackProject.Length > 0)
        {
            await plane.ProjectIdAsync(Settings.Plane.FallbackProject, ct);
            return $"workspace {Settings.Plane.Workspace}, project {Settings.Plane.FallbackProject} found";
        }

        return $"workspace {Settings.Plane.Workspace}, {await plane.ProjectCountAsync(ct)} projects";
    }

    protected override string Fix(Exception ex) => ex is InvalidOperationException
        ? $"Create that project in Plane, or fix {Hints.Env("Plane:FallbackProject")}."
        : Hints.Http(ex, "Plane:BaseUrl", "Plane:Token");
}

internal sealed class KarakeepCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : ServiceCheck(services, options)
{
    public override string Name => "karakeep";
    protected override string? OffReason => Settings.Karakeep.Enabled ? null : "not configured";

    protected override async Task<string> ProbeAsync(CancellationToken ct)
    {
        await Services.GetRequiredService<KarakeepClient>().PingAsync(ct);
        return $"{Hints.Host(Settings.Karakeep.BaseUrl)} answers";
    }

    protected override string Fix(Exception ex) => Hints.Http(ex, "Karakeep:BaseUrl", "Karakeep:Token");
}

internal sealed class GitHubCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : ServiceCheck(services, options)
{
    public override string Name => "github";
    protected override string? OffReason => Settings.GitHub.Enabled ? null : "not configured";

    protected override async Task<string> ProbeAsync(CancellationToken ct)
    {
        await Services.GetRequiredService<GitHubStarsClient>().PingAsync(ct);
        return $"user {Settings.GitHub.User} found";
    }

    protected override string Fix(Exception ex) => ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
        ? $"GitHub has no user with that name: check {Hints.Env("GitHub:User")}."
        : Hints.Http(ex, "GitHub:BaseUrl", null);
}

internal sealed class HindsightCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : ServiceCheck(services, options)
{
    public override string Name => "hindsight";
    protected override string? OffReason => Settings.Hindsight.Enabled ? null : "not configured";

    protected override async Task<string> ProbeAsync(CancellationToken ct)
    {
        await Services.GetRequiredService<HindsightClient>().PingAsync(ct);
        return $"{Hints.Host(Settings.Hindsight.BaseUrl)} answers";
    }

    protected override string Fix(Exception ex) => Hints.Http(ex, "Hindsight:BaseUrl", "Hindsight:Token");
}

/// <summary>Loads what the owner runs from the PINS source or the fallback list; none loaded means no release can be matched.</summary>
internal sealed class WatchCheck(IServiceProvider services, IOptions<FeedEaterOptions> options) : ServiceCheck(services, options)
{
    public override string Name => "release watch";
    protected override string? OffReason => Settings.Watch.Enabled ? null : "not configured";

    protected override async Task<string> ProbeAsync(CancellationToken ct)
    {
        var products = await Services.GetRequiredService<WatchSource>().LoadAsync(ct);
        return products.Count > 0
            ? $"{products.Count} products watched"
            : throw new InvalidOperationException("no pinned image maps to an upstream repo");
    }

    protected override string Fix(Exception ex) =>
        $"Check {Hints.Env("Watch:Source")} (a PINS.md path or https URL) or {Hints.Env("Watch:FallbackPath")} (see config/watch.example.json), and the image map in config/watch-map.json.";
}
