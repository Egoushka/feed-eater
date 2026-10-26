using System.Globalization;
using FeedEater.Digest;
using FeedEater.Follow;
using FeedEater.Loops;
using FeedEater.Ranking;
using FeedEater.Sources;
using FeedEater.Storage;
using FeedEater.Ui;
using FeedEater.WeeklyIdeas;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FeedEater.Api;

public static class ManageApi
{
    private static readonly TimeSpan StatsWindow = TimeSpan.FromDays(30);

    /// <summary>Follows, feeds, settings, and on-demand digest and ideas runs.</summary>
    public static RouteGroupBuilder MapManageApi(this RouteGroupBuilder group)
    {
        group.MapGet("/follows", async (StoryFollower follower, CancellationToken ct) =>
                Results.Ok(follower.Enabled ? (await follower.ActiveAsync(ct)).Select(Follow).ToList() : []))
            .WithName("ListFollows")
            .Produces<IReadOnlyList<FollowResponse>>();

        group.MapPost("/follows", async (StartFollowBody body, StoryFollower follower, CancellationToken ct) =>
            {
                if (body.ItemId is not { } itemId)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "itemId is required");
                }

                return await follower.StartAsync(itemId, ct) switch
                {
                    FollowOutcome.Off => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Following stories is off"),
                    var outcome => Results.Ok(new StartFollowResponse(outcome.ToString())),
                };
            })
            .WithName("StartFollow")
            .Produces<StartFollowResponse>();

        group.MapDelete("/follows/{id:long}", async (long id, StoryFollower follower, CancellationToken ct) =>
                await follower.CloseAsync(id, null, ct) ? Results.NoContent() : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such active follow"))
            .WithName("StopFollow");

        group.MapGet("/feeds", ListFeedsAsync)
            .WithName("ListFeeds")
            .Produces<IReadOnlyList<FeedResponse>>();

        group.MapPost("/feeds", async (AddFeedBody body, [FromServices] FeedManager? manager, CancellationToken ct) =>
            {
                if (manager is null)
                {
                    return Miniflux();
                }

                if (string.IsNullOrWhiteSpace(body.Url))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "url is required");
                }

                var result = await manager.AddAsync(body.Url, ct);
                return result.Ok
                    ? Results.Json(new AddFeedResponse(result.Title, result.Existing, result.Items), statusCode: StatusCodes.Status201Created)
                    : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
            })
            .WithName("AddFeed")
            .Produces<AddFeedResponse>(StatusCodes.Status201Created);

        group.MapPost("/feeds/{id:long}/mute", async (long id, MuteFeedBody body, ItemStore items, CancellationToken ct) =>
            {
                if (body.Muted is not { } muted)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "muted is required");
                }

                return await items.SetFeedMutedAsync(id, muted, ct) ? Results.Ok(new OkResponse(true)) : NoFeed();
            })
            .WithName("MuteFeed")
            .Produces<OkResponse>();

        group.MapDelete("/feeds/{id:long}", async (long id, [FromServices] FeedStore? feeds, CancellationToken ct) =>
            {
                if (feeds is null)
                {
                    return Miniflux();
                }

                return await feeds.RemoveAsync(id, ct) ? Results.NoContent() : NoFeed();
            })
            .WithName("RemoveFeed");

        group.MapGet("/settings", SettingsAsync)
            .WithName("GetSettings")
            .Produces<SettingsResponse>();

        group.MapPut("/settings", async (SettingsBody body, QuietHours quiet, TasteSwitch taste, IOptions<FeedEaterOptions> options, CancellationToken ct) =>
            {
                if (body.Quiet is { } on)
                {
                    await quiet.SetAsync(on, ct);
                }

                if (body.Learn is { } learn)
                {
                    await taste.SetAsync(learn, ct);
                }

                return await SettingsAsync(quiet, taste, options, ct);
            })
            .WithName("PutSettings")
            .Produces<SettingsResponse>();

        group.MapPost("/digest/run", async (RunDigestBody? body, DigestTrigger trigger, IOptions<FeedEaterOptions> options, CancellationToken ct) =>
            {
                if (!Delivers(options.Value))
                {
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The digest job is not running");
                }

                await trigger.RequestAsync(body?.Resend ?? false, ct);
                return Results.Accepted();
            })
            .WithName("RunDigest");

        group.MapPost("/ideas/run", async (CursorStore cursors, IOptions<FeedEaterOptions> options, CancellationToken ct) =>
            {
                var settings = options.Value;
                if (!Delivers(settings) || !settings.WeeklyIdeas.Enabled || !settings.GitHub.IssuesEnabled)
                {
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Weekly ideas are off");
                }

                await cursors.SetAsync(WeeklyIdeasJob.ForceCursor, "1", ct);
                return Results.Accepted();
            })
            .WithName("RunIdeas");

        return group;
    }

    /// <summary>The digest and the weekly jobs run only with background jobs on and a place to deliver to.</summary>
    private static bool Delivers(FeedEaterOptions settings) =>
        settings.RunJobs && (settings.Telegram.Token.Length > 0 || settings.Swipe.Enabled);

    private static IResult Miniflux() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Add or remove feeds in Miniflux");

    private static IResult NoFeed() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such feed");

    private static FollowResponse Follow(ActiveFollow f) =>
        new(f.Id, f.RootItemId, f.Title, Html.SafeUrl(f.Url), Utc(f.StartedAt), Utc(f.EndsAt), f.Sent);

    private static DateTimeOffset Utc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    // [FromServices]: the reader is unregistered in Miniflux mode, and an unregistered type would be inferred as a body.
    private static async Task<IResult> ListFeedsAsync(ItemStore items, [FromServices] FeedStore? feeds, TimeProvider time, CancellationToken ct)
    {
        var all = await items.FeedsAsync(ct);
        var stats = (await items.SourceStatsAsync(time.GetUtcNow() - StatsWindow, ct)).ToDictionary(s => s.FeedId);
        var states = feeds is null ? [] : (await feeds.ListAsync(ct)).ToDictionary(s => s.Id);
        return Results.Ok(all.Select(f =>
        {
            stats.TryGetValue(f.Id, out var s);
            states.TryGetValue(f.Id, out var state);
            var up = s?.Up ?? 0;
            var down = s?.Down ?? 0;
            return new FeedResponse(
                f.Id, f.Title, f.Category, Html.SafeUrl(f.SiteUrl), Html.SafeUrl(f.FeedUrl), f.Muted,
                s?.Items ?? 0, s?.Shown ?? 0, up, down, up + down > 0 ? (double)up / (up + down) : null,
                state?.FailCount, state?.LastError);
        }).ToList());
    }

    private static async Task<IResult> SettingsAsync(QuietHours quiet, TasteSwitch taste, IOptions<FeedEaterOptions> options, CancellationToken ct)
    {
        var settings = options.Value;
        var status = await quiet.StatusAsync(ct);
        var window = quiet.WindowConfigured;
        return Results.Ok(new SettingsResponse(
            status.Quiet, status.Text, window,
            window ? Clock(settings.Quiet.From!.Value) : null, window ? Clock(settings.Quiet.To!.Value) : null,
            await taste.IsOnAsync(ct), await taste.StatusAsync(ct), Clock(settings.DigestAt)));
    }

    private static string Clock(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
