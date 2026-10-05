using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Memory;
using FeedEater.Mcp;
using FeedEater.Plane;
using FeedEater.Profiles;
using FeedEater.Search;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Telegram;

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
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<FeedbackStore>();
        services.AddSingleton<AnalysisStore>();
        services.AddSingleton<DigestStore>();
        services.AddSingleton<SignalStore>();
        services.AddSingleton<UsageStore>();
        services.AddSingleton<IUsageSink>(sp => sp.GetRequiredService<UsageStore>());
        services.AddSingleton<LoopHealth>();

        services.AddHttpClient<LiteLlmClient>((sp, http) =>
        {
            var o = Settings(sp).Llm;
            http.BaseAddress = new Uri(o.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(120);
            Bearer(http, o.ApiKey);
        });
        services.AddHttpClient<MinifluxClient>((sp, http) =>
        {
            var o = Settings(sp).Miniflux;
            http.BaseAddress = new Uri(o.BaseUrl);
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
            http.BaseAddress = new Uri(o.BaseUrl);
            http.DefaultRequestHeaders.Add("X-API-Key", o.Token);
        });
        services.AddHttpClient<KarakeepClient>((sp, http) =>
        {
            var o = Settings(sp).Karakeep;
            http.BaseAddress = new Uri(o.BaseUrl);
            Bearer(http, o.Token);
        });
        services.AddHttpClient<GitHubStarsClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(Settings(sp).GitHub.BaseUrl);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("feed-eater/0.2");
        });

        services.AddHttpClient<HindsightClient>((sp, http) =>
        {
            var o = Settings(sp).Hindsight;
            http.BaseAddress = new Uri(o.BaseUrl);
            Bearer(http, o.Token);
        });

        services.AddSingleton<DigestRun>();
        services.AddSingleton<IdeaFiler>();
        services.AddSingleton<CallbackHandler>();
        services.AddSingleton<ArchiveSearch>();
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<DigestTrigger>();

        if (settings.RunJobs)
        {
            services.AddHostedService<Ingestor>();
            services.AddHostedService<ProfileBuilder>();
            services.AddHostedService<SignalJob>();

            // Without a bot token a digest would pay for triage and then fail to send.
            var telegram = settings.Telegram.Token.Length > 0;
            if (telegram)
            {
                services.AddHostedService<DigestJob>();
            }

            services.AddHostedService<WeeklyRetain>();
            if (telegram)
            {
                services.AddHostedService<TelegramPoller>();
            }
        }

        services.AddFeedEaterMcp();
        return services;
    }

    private static FeedEaterOptions Settings(IServiceProvider sp) => sp.GetRequiredService<IOptions<FeedEaterOptions>>().Value;

    private static void Bearer(HttpClient http, string token)
    {
        if (token.Length > 0)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
