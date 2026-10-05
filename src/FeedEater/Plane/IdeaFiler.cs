using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Plane;

/// <summary>Turns an item's suggestion into a Plane Intake item in the matched project's Plane project, else FEED. Once per item.</summary>
public sealed class IdeaFiler(
    ItemStore items, FeedbackStore feedback, ProfileStore profiles, PlaneClient plane, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    private const int MaxTitle = 80;

    public async Task<string> FileAsync(long itemId, CancellationToken ct)
    {
        if (await feedback.GetIdeaAsync(itemId, ct) is { } existing)
        {
            return existing.PlaneProject;
        }

        var item = await items.GetAsync(itemId, ct) ?? throw new InvalidOperationException($"item {itemId} does not exist");
        if (item.Suggestion is null)
        {
            throw new InvalidOperationException("this item has no suggestion to file");
        }

        var key = item.Project ?? item.ProfileKey;
        var project = (await profiles.AllAsync(ct)).FirstOrDefault(p => p.Key == key)?.PlaneIdentifier
            ?? options.Value.Plane.FallbackProject;
        var title = item.Suggestion.Length <= MaxTitle ? item.Suggestion : item.Suggestion[..(MaxTitle - 1)] + "…";
        var html = $"<p>{E(item.Suggestion)}</p><p>{E(item.Why ?? "")}</p>"
            + $"<p>Source: <a href=\"{E(item.Url)}\">{E(item.Title)}</a> ({E(item.Feed)}, feed-eater item {item.Id})</p>";
        var issueId = await plane.CreateIntakeAsync(project, title, html, ct);
        await feedback.AddIdeaAsync(new Idea { ItemId = itemId, PlaneProject = project, PlaneIssueId = issueId, Title = title, At = time.GetUtcNow().UtcDateTime }, ct);
        return project;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
