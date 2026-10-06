using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FeedEater.Digest;
using FeedEater.Duels;
using FeedEater.Ingest;
using FeedEater.Memory;
using FeedEater.Plane;
using FeedEater.Profiles;
using FeedEater.Review;
using FeedEater.Watch;
using FeedEater.Signals;
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class CompositionTests
{
    private static readonly Dictionary<string, string?> AllIntegrations = new()
    {
        ["FeedEater:Karakeep:BaseUrl"] = "http://karakeep/",
        ["FeedEater:Karakeep:Token"] = "karakeep-token",
        ["FeedEater:GitHub:User"] = "octocat",
        ["FeedEater:Hindsight:BaseUrl"] = "http://hindsight/",
        ["FeedEater:Watch:FallbackPath"] = "/watch.json",
        ["FeedEater:Plane:BaseUrl"] = "http://plane/",
        ["FeedEater:Plane:Token"] = "plane-token",
        ["FeedEater:Plane:Workspace"] = "ws",
    };

    /// <summary>Builds the container the way the host does, but never starts it.</summary>
    private static IHost Build(bool runJobs, string telegramToken = "123:test", IReadOnlyDictionary<string, string?>? extra = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FeedEater"] = "Host=localhost;Database=unused",
            ["FeedEater:RunJobs"] = runJobs.ToString(),
            ["FeedEater:Telegram:Token"] = telegramToken,
        });
        builder.Configuration.AddInMemoryCollection(extra ?? new Dictionary<string, string?>());
        builder.Services.AddFeedEater(builder.Configuration);
        return builder.Build();
    }

    private static Type[] Jobs(IHost host) => host.Services.GetServices<IHostedService>()
        .Select(s => s.GetType()).Where(t => t.Namespace!.StartsWith("FeedEater", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void Every_background_job_resolves_when_jobs_are_enabled_and_every_integration_is_configured()
    {
        using var host = Build(runJobs: true, extra: AllIntegrations);

        Assert.Equivalent(new[] { typeof(Ingestor), typeof(ProfileBuilder), typeof(SignalJob), typeof(DigestJob), typeof(WeeklyRetain), typeof(ReleaseWatcher), typeof(WeeklyReview), typeof(DuelJob), typeof(TelegramPoller) }, Jobs(host));
    }

    [Fact]
    public void No_background_job_is_registered_when_jobs_are_disabled()
    {
        using var host = Build(runJobs: false, extra: AllIntegrations);

        Assert.Empty(Jobs(host));
    }

    [Fact]
    public void Digest_and_poller_are_not_registered_without_a_token()
    {
        using var host = Build(runJobs: true, telegramToken: "", extra: AllIntegrations);

        Assert.Equivalent(new[] { typeof(Ingestor), typeof(ProfileBuilder), typeof(SignalJob), typeof(WeeklyRetain), typeof(ReleaseWatcher) }, Jobs(host));
    }

    [Fact]
    public void With_no_optional_integration_configured_only_the_core_jobs_run()
    {
        using var host = Build(runJobs: true);

        Assert.Equivalent(new[] { typeof(Ingestor), typeof(ProfileBuilder), typeof(DigestJob), typeof(WeeklyReview), typeof(DuelJob), typeof(TelegramPoller) }, Jobs(host));
    }

    [Fact]
    public void The_duel_is_off_with_its_switch()
    {
        using var host = Build(runJobs: true, extra: new Dictionary<string, string?> { ["FeedEater:Duel:Enabled"] = "false" });

        Assert.DoesNotContain(typeof(DuelJob), Jobs(host));
    }

    [Theory]
    [InlineData("FeedEater:Karakeep:BaseUrl", "http://karakeep/", typeof(SignalJob), false)]   // the API key is missing too
    [InlineData("FeedEater:GitHub:User", "octocat", typeof(SignalJob), true)]
    [InlineData("FeedEater:Hindsight:BaseUrl", "http://hindsight/", typeof(WeeklyRetain), true)]
    [InlineData("FeedEater:Watch:FallbackPath", "/watch.json", typeof(ReleaseWatcher), true)]
    [InlineData("FeedEater:Watch:Source", "/PINS.md", typeof(ReleaseWatcher), true)]
    public void An_integration_registers_its_job_only_once_its_setting_is_filled(string key, string value, Type job, bool registered)
    {
        using var host = Build(runJobs: true, extra: new Dictionary<string, string?> { [key] = value });

        Assert.Equal(registered, Jobs(host).Contains(job));
    }

    [Fact]
    public void A_configured_integration_is_off_again_when_its_setting_is_blank()
    {
        using var host = Build(runJobs: true, extra: new Dictionary<string, string?> { ["FeedEater:Hindsight:BaseUrl"] = "", ["FeedEater:Watch:Source"] = "" });

        Assert.DoesNotContain(typeof(WeeklyRetain), Jobs(host));
        Assert.DoesNotContain(typeof(ReleaseWatcher), Jobs(host));
    }

    [Fact]
    public void The_idea_sink_is_local_without_Plane_and_Plane_once_it_is_configured()
    {
        using var without = Build(runJobs: false);
        using var with = Build(runJobs: false, extra: AllIntegrations);
        using var forced = Build(runJobs: false, extra: new Dictionary<string, string?>(AllIntegrations) { ["FeedEater:Ideas:Sink"] = "local" });

        Assert.IsType<LocalIdeaSink>(without.Services.GetRequiredService<IIdeaSink>());
        Assert.IsType<PlaneIdeaSink>(with.Services.GetRequiredService<IIdeaSink>());
        Assert.IsType<LocalIdeaSink>(forced.Services.GetRequiredService<IIdeaSink>());
    }

    [Fact]
    public void Clients_of_integrations_that_are_off_still_resolve()
    {
        using var host = Build(runJobs: true);

        Assert.NotNull(host.Services.GetRequiredService<DigestRun>());
        Assert.NotNull(host.Services.GetRequiredService<IdeaFiler>());
        Assert.NotNull(host.Services.GetRequiredService<CallbackHandler>());
        Assert.NotNull(host.Services.GetRequiredService<ReplyHandler>());
        Assert.NotEmpty(Jobs(host));
    }
}
