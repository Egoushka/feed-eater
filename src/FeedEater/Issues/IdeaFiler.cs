using FeedEater.Storage;

namespace FeedEater.Issues;

/// <summary>Turns an item's suggestion into an idea in the matched project, else the sink's fallback project. Once per item.</summary>
public sealed class IdeaFiler(
    ItemStore items, FeedbackStore feedback, ProfileStore profiles, IIdeaSink sink, TimeProvider time)
{
    private const int MaxTitle = 80;

    public IIdeaSink Sink => sink;

    public Task<string> FileAsync(long itemId, CancellationToken ct) => FileAsync(itemId, null, null, ct);

    /// <summary>
    /// Files the item's suggestion, or <paramref name="text"/> when given, in <paramref name="project"/> when given, else in the
    /// item's project. An item already filed returns where it went and files nothing.
    /// </summary>
    public async Task<string> FileAsync(long itemId, string? project, string? text, CancellationToken ct)
    {
        if (await feedback.GetIdeaAsync(itemId, ct) is { } existing)
        {
            return existing.Project;
        }

        var item = await items.GetAsync(itemId, ct) ?? throw new InvalidOperationException($"item {itemId} does not exist");
        var idea = text ?? item.Suggestion ?? throw new InvalidOperationException("this item has no suggestion to file");
        var key = item.Project ?? item.ProfileKey;
        project ??= sink.ProjectOf((await profiles.AllAsync(ct)).FirstOrDefault(p => p.Key == key))
            ?? sink.FallbackProject
            ?? throw new InvalidOperationException("this item matches no project and no fallback repo is set (GitHub:FallbackRepo)");
        var title = idea.Length <= MaxTitle ? idea : idea[..(MaxTitle - 1)] + "…";
        var body = $"{Markdown.Text(idea)}\n\n{Markdown.Text(item.Why ?? "")}\n\nSource: {Markdown.Link(item.Title, item.Url)} ({Markdown.Text(item.Feed)}, feed-eater item {item.Id})";
        var number = await sink.FileAsync(new IdeaDraft(project, title, body), ct);
        await feedback.AddIdeaAsync(new Idea { ItemId = itemId, Project = project, IssueNumber = number, Title = title, At = time.GetUtcNow().UtcDateTime }, ct);
        return project;
    }
}
