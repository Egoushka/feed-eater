using FeedEater.Ui;

namespace FeedEater.Setup;

public static class SetupServices
{
    /// <summary>
    /// The one list of checks that <c>doctor</c> and <c>/ui/setup</c> both run, in the order they are shown. A feed-source mode replaces
    /// <see cref="IFeedSourceProbe"/> by registering its own after this call.
    /// </summary>
    public static IServiceCollection AddSetupChecks(this IServiceCollection services)
    {
        services.AddSingleton<IFeedSourceProbe, DefaultFeedSourceProbe>();
        services.AddSingleton<IIntegrationCheck, DatabaseCheck>();
        services.AddSingleton<IIntegrationCheck, LlmChatCheck>();
        services.AddSingleton<IIntegrationCheck, LlmEmbeddingCheck>();
        services.AddSingleton<IIntegrationCheck, TelegramCheck>();
        services.AddSingleton<IIntegrationCheck, FeedSourceCheck>();
        services.AddSingleton<IIntegrationCheck, ProfileCheck>();
        services.AddSingleton<IIntegrationCheck, MinifluxCheck>();
        services.AddSingleton<IIntegrationCheck, PlaneCheck>();
        services.AddSingleton<IIntegrationCheck, KarakeepCheck>();
        services.AddSingleton<IIntegrationCheck, GitHubCheck>();
        services.AddSingleton<IIntegrationCheck, HindsightCheck>();
        services.AddSingleton<IIntegrationCheck, WatchCheck>();
        services.AddSingleton<CheckRunner>();
        services.AddSingleton<SetupHandler>();
        return services;
    }
}
