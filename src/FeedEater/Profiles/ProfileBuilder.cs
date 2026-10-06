using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Storage;

namespace FeedEater.Profiles;

/// <summary>Daily at 03:00 (and at start when today's build is missing): one vector per project and topic.</summary>
public sealed class ProfileBuilder(
    PlaneClient plane, ProfileStore profiles, LiteLlmClient llm,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<ProfileBuilder> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private const int MaxTitles = 30;

    protected override string Name => "profiles";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, new TimeSpan(3, 0, 0));

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var file = ProfileFile.LoadOrExample(Settings.ProfilePath).File;
        var entries = new List<(ProfileEntry Entry, string Kind, string Text)>();
        foreach (var p in file.Projects)
        {
            entries.Add((p, "project", await ProjectTextAsync(p, ct)));
        }

        entries.AddRange(file.Topics.Select(t => (t, "topic", t.Description)));

        var vectors = await llm.EmbedAsync(entries.Select(e => e.Text).ToList(), "profile", ct);
        await profiles.ReplaceAllAsync(entries.Select((e, i) => new Profile
        {
            Key = e.Entry.Key,
            Kind = e.Kind,
            PlaneIdentifier = e.Entry.Plane,
            Description = e.Entry.Description,
            Embedding = vectors[i].ToArray(),
        }).ToList(), Time.GetUtcNow(), ct);
        Logger.LogInformation("Profiles rebuilt: {Count}", entries.Count);
    }

    private async Task<string> ProjectTextAsync(ProfileEntry project, CancellationToken ct)
    {
        if (project.Plane is null || !Settings.Plane.Enabled)
        {
            return project.Description;
        }

        try
        {
            var titles = await plane.OpenItemTitlesAsync(project.Plane, ct);
            return titles.Count == 0 ? project.Description : $"{project.Description}\nOpen work: {string.Join("; ", titles.Take(MaxTitles))}";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException or JsonException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Logger.LogWarning(ex, "Plane work items for {Project} unavailable; using the description only", project.Plane);
            return project.Description;
        }
    }
}
