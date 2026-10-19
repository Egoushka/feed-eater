using System.Reflection;
using FeedEater.Digest;
using FeedEater.Storage;
using FeedEater.Swipe;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FeedEater.Api;

public sealed record StatusResponse(string Version, string Gate, DateTimeOffset? GateUntil, string? GateWhy, DigestStatus? Digest, bool Telegram, bool Swipe);

public sealed record DigestStatus(string Date, string Status, int Items, DateTimeOffset? SentAt);

public static class ApiEndpoints
{
    public const string Prefix = "/api/v1";

    /// <summary>
    /// The app API: bearer tokens only, no browsers, one rate limit per token, bodies up to 64 KB. Returns the group so each area adds its own routes.
    /// </summary>
    public static RouteGroupBuilder MapFeedEaterApi(this IEndpointRouteBuilder app)
    {
        // Metadata only, no secrets: stays outside the auth filter.
        app.MapOpenApi(Prefix + "/openapi.json");

        var group = app.MapGroup(Prefix);
        group.AddEndpointFilter<ApiErrors>();
        group.AddEndpointFilter<ApiAuth>();
        group.WithMetadata(new RequestSizeLimitAttribute(ApiErrors.MaxBodyBytes));

        group.MapGet("/status", StatusAsync)
            .WithName("GetStatus")
            .Produces<StatusResponse>();
        group.MapSwipeApi();

        return group;
    }

    private static async Task<StatusResponse> StatusAsync(
        SwipeService swipe, DigestStore digests, DigestTrigger trigger, IOptions<FeedEaterOptions> options, CancellationToken ct)
    {
        var settings = options.Value;
        var gate = "off";
        DateTimeOffset? until = null;
        string? why = null;
        if (settings.Swipe.Enabled)
        {
            var decision = await swipe.GateAsync(ct);
            gate = decision.Gate switch
            {
                Gate.Summary => "summary",
                Gate.Closed => "closed",
                Gate.Resume or Gate.Expire or Gate.Start => "open",
                var other => throw new InvalidOperationException($"Unknown gate {other}"),
            };
            until = decision.Until;
            why = decision.Why;
        }

        var row = await digests.GetAsync(trigger.Today(), ct);
        var digest = row is null ? null : new DigestStatus(row.LocalDate, row.Status, row.ItemIds.Length, row.SentAt is { } sent ? new DateTimeOffset(DateTime.SpecifyKind(sent, DateTimeKind.Utc)) : null);
        return new StatusResponse(Version(), gate, until, why, digest, settings.Telegram.Token.Length > 0, settings.Swipe.Enabled);
    }

    private static string Version()
    {
        var info = typeof(ApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = info?.Split('+')[0];
        return string.IsNullOrEmpty(version) ? "dev" : version;
    }
}
