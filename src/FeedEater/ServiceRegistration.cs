using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Digest;
using FeedEater.Eval;
using FeedEater.Fetch;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Memory;
using FeedEater.Mcp;
using FeedEater.Plane;
using FeedEater.Profiles;
using FeedEater.Ranking;
using FeedEater.Review;
using FeedEater.Watch;
using FeedEater.Search;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Ui;

namespace FeedEater;

public static class ServiceRegistration
{
    public static IServiceCollection AddFeedEater(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FeedEater")
            ?? throw new InvalidOperationException("ConnectionStrings:FeedEater is not set.");
        var section = configuration.GetSection(FeedEaterOptions.Section);
        var settings = section.Get<FeedEaterOptions>() ?? new FeedEaterOptions();

        services.Configure<FeedEaterOptions>(section);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DatabaseMigrator(connectionString, sp.GetRequiredService<ILogger<DatabaseMigrator>>()));
        services.AddSingleton(new FeedDb(NpgsqlDataSource.Create(connectionString)));
        services.AddSingleton<CursorStore>();
        services.AddSingleton<ItemStore>();
        services.AddSingleton<ClusterStore>();
        services.AddSingleton<StoryClusterer>();
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<FeedbackStore>();
        services.AddSingleton<AnalysisStore>();
        services.AddSingleton<DigestStore>();
        services.AddSingleton<SignalStore>();
        services.AddSingleton<WeeklyStore>();
        services.AddSingleton<ReleaseStore>();
        services.AddSingleton<QuietHours>();
        services.AddSingleton<UsageStore>();
        services.AddSingleton<IUsageSink>(sp => sp.GetRequiredService<UsageStore>());
        services.AddSingleton<LoopHealth>();

        services.AddHttpClient<LiteLlmClient>((sp, http) =>
        {
            var o = Settings(sp).Llm;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");   // request paths are relative to it, so a missing slash would drop its last segment
            http.Timeout = TimeSpan.FromSeconds(120);
            Bearer(http, o.ApiKey);
        });
        services.AddHttpClient<MinifluxClient>((sp, http) =>
        {
            var o = Settings(sp).Miniflux;
            BaseAddress(http, o.BaseUrl);
            http.DefaultRequestHeaders.Add("X-Auth-Token", o.Token);
        });
        services.AddHttpClient<TelegramClient>((sp, http) =>
        {
            var o = Settings(sp).Telegram;
            http.BaseAddress = new Uri($"{o.BaseUrl}bot{o.Token}/");
            http.Timeout = TimeSpan.FromSeconds(70);   // above the 50 s long poll
        });
        services.AddHttpClient<PlaneClient>((sp, http) =>
        {
            var o = Settings(sp).Plane;
            BaseAddress(http, o.BaseUrl);
            http.DefaultRequestHeaders.Add("X-API-Key", o.Token);
        });
        services.AddHttpClient<KarakeepClient>((sp, http) =>
        {
            var o = Settings(sp).Karakeep;
            BaseAddress(http, o.BaseUrl);
            Bearer(http, o.Token);
        });
        services.AddHttpClient<GitHubStarsClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(Settings(sp).GitHub.BaseUrl);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("feed-eater/0.5.0");
        });

        services.AddHttpClient(SafeFetcher.ClientName).ConfigurePrimaryHttpMessageHandler(SafeFetcher.CreateHandler);
        services.AddSingleton(sp => new SafeFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(SafeFetcher.ClientName), sp.GetRequiredService<IOptions<FeedEaterOptions>>(),
            sp.GetRequiredService<CursorStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SafeFetcher>>()));
        services.AddHttpClient<HnClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(Settings(sp).Fetch.HnApiBase);
            http.Timeout = TimeSpan.FromSeconds(10);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        });
        services.AddSingleton<PageEnricher>();
        services.AddSingleton<DiscoveryStore>();
        services.AddSingleton<EvalStore>();
        services.AddSingleton<EvalRunner>();
        services.AddSingleton<FeedDiscoverer>();
        // The client carries the PINS bearer token, so it must never follow a redirect to another host.
        services.AddHttpClient<WatchSource>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddHttpClient<HindsightClient>((sp, http) =>
        {
            var o = Settings(sp).Hindsight;
            BaseAddress(http, o.BaseUrl);
            Bearer(http, o.Token);
        });

        services.AddSingleton<DigestRun>();
        services.AddSingleton<PlaneIdeaSink>();
        services.AddSingleton<LocalIdeaSink>();
        services.AddSingleton<IIdeaSink>(sp => Settings(sp).IdeaSink == IdeasOptions.PlaneSink
            ? sp.GetRequiredService<PlaneIdeaSink>()
            : sp.GetRequiredService<LocalIdeaSink>());
        services.AddSingleton<IdeaFiler>();
        services.AddSingleton<CallbackHandler>();
        services.AddSingleton<ArchiveSearch>();
        services.AddSingleton<ArchiveAnswer>();
        services.AddSingleton<ReplyHandler>();
        services.AddSingleton<TasteSwitch>();
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<DigestTrigger>();

        if (settings.RunJobs)
        {
            services.AddHostedService<Ingestor>();
            services.AddHostedService<ProfileBuilder>();
            if (settings.Karakeep.Enabled || settings.GitHub.Enabled)
            {
                services.AddHostedService<SignalJob>();
            }

            // Without a bot token a digest would pay for triage and then fail to send.
            var telegram = settings.Telegram.Token.Length > 0;
            if (telegram)
            {
                services.AddHostedService<DigestJob>();
            }

            if (settings.Hindsight.Enabled)
            {
                services.AddHostedService<WeeklyRetain>();
            }

            if (settings.Watch.Enabled)
            {
                services.AddHostedService<ReleaseWatcher>();
            }

            if (telegram)
            {
                services.AddHostedService<WeeklyReview>();
                services.AddHostedService<TelegramPoller>();
            }
        }

        services.AddSingleton(sp => new UiSession(configuration["Mcp:Token"], sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<UiHandlers>();

        services.AddFeedEaterMcp();
        return services;
    }

    private static FeedEaterOptions Settings(IServiceProvider sp) => sp.GetRequiredService<IOptions<FeedEaterOptions>>().Value;

    /// <summary>An integration that is off has no URL; its client is never called, and must still be buildable.</summary>
    private static void BaseAddress(HttpClient http, string url)
    {
        if (url.Length > 0)
        {
            http.BaseAddress = new Uri(url);
        }
    }

    private static void Bearer(HttpClient http, string token)
    {
        if (token.Length > 0)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
