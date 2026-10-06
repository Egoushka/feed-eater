using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Memory;
using FeedEater.Profiles;
using FeedEater.Review;
using FeedEater.Watch;
using FeedEater.Signals;
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class CompositionTests
{
    /// <summary>Builds the container the way the host does, but never starts it.</summary>
    private static IHost Build(bool runJobs, string telegramToken = "123:test")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FeedEater"] = "Host=localhost;Database=unused",
            ["FeedEater:RunJobs"] = runJobs.ToString(),
            ["FeedEater:Telegram:Token"] = telegramToken,
            ["FeedEater:Karakeep:Token"] = "karakeep-token",
        });
        builder.Services.AddFeedEater(builder.Configuration);
        return builder.Build();
    }

    private static Type[] Jobs(IHost host) => host.Services.GetServices<IHostedService>()
        .Select(s => s.GetType()).Where(t => t.Namespace!.StartsWith("FeedEater", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void Every_background_job_resolves_when_jobs_are_enabled()
    {
        using var host = Build(runJobs: true);

        Assert.Equivalent(new[] { typeof(Ingestor), typeof(ProfileBuilder), typeof(SignalJob), typeof(DigestJob), typeof(WeeklyRetain), typeof(ReleaseWatcher), typeof(WeeklyReview), typeof(TelegramPoller) }, Jobs(host));
    }

    [Fact]
    public void No_background_job_is_registered_when_jobs_are_disabled()
    {
        using var host = Build(runJobs: false);

        Assert.Empty(Jobs(host));
    }

    [Fact]
    public void Digest_and_poller_are_not_registered_without_a_token()
    {
        using var host = Build(runJobs: true, telegramToken: "");

        Assert.Equivalent(new[] { typeof(Ingestor), typeof(ProfileBuilder), typeof(SignalJob), typeof(WeeklyRetain), typeof(ReleaseWatcher) }, Jobs(host));
    }
}
