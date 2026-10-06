namespace FeedEater.Sources;

public static class SourceRegistration
{
    /// <summary>
    /// The built-in reader. In Miniflux mode nothing here is registered, which is what switches the ingest loop, the digest and the
    /// UI back to their Miniflux behaviour: each takes these services as optional.
    /// </summary>
    public static IServiceCollection AddFeedSource(this IServiceCollection services, SourceOptions source)
    {
        if (!source.IsBuiltin)
        {
            return services;
        }

        services.AddSingleton<FeedStore>();
        services.AddSingleton<FeedReader>();
        services.AddSingleton<FeedPoller>();
        services.AddSingleton<FeedManager>();
        services.AddSingleton<FeedsCommand>();
        services.AddSingleton<ArticleText>();
        services.AddSingleton<SourcesUi>();
        return services;
    }
}
