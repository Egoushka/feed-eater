using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Plane;

/// <summary>An idea ready to go to a sink: the project it belongs to, a short title and the same text as HTML for sinks that take it.</summary>
public sealed record IdeaDraft(string Project, string Title, string Html);

/// <summary>Where a filed idea goes. <see cref="IdeaFiler"/> records every idea in the <c>ideas</c> table; the sink does the rest.</summary>
public interface IIdeaSink
{
    string Name { get; }

    /// <summary>The project an idea about <paramref name="profile"/> goes to; null when it has none of its own.</summary>
    string? ProjectOf(Profile? profile);

    /// <summary>The project for an idea that matches no profile; null when the sink has none.</summary>
    string? FallbackProject { get; }

    /// <summary>What the reader is told once an idea went to <paramref name="project"/>.</summary>
    string Filed(string project);

    /// <summary>Stores the idea and returns the sink's id for it (empty when the sink keeps none).</summary>
    Task<string> FileAsync(IdeaDraft idea, CancellationToken ct);
}

/// <summary>Plane Intake: an idea becomes an intake item in a Plane project.</summary>
public sealed class PlaneIdeaSink(PlaneClient plane, IOptions<FeedEaterOptions> options) : IIdeaSink
{
    public string Name => IdeasOptions.PlaneSink;
    public string? ProjectOf(Profile? profile) => profile?.PlaneIdentifier;
    public string? FallbackProject => options.Value.Plane.FallbackProject is { Length: > 0 } fallback ? fallback : null;
    public string Filed(string project) => $"Filed in {project}";

    public Task<string> FileAsync(IdeaDraft idea, CancellationToken ct) => options.Value.Plane.Enabled
        ? plane.CreateIntakeAsync(idea.Project, idea.Title, idea.Html, ct)
        : throw new InvalidOperationException("Plane is not configured");
}

/// <summary>The default without Plane: the idea stays in the archive's own list (/ui/ideas, the feed_ideas MCP tool).</summary>
public sealed class LocalIdeaSink : IIdeaSink
{
    public const string Inbox = "inbox";

    public string Name => IdeasOptions.LocalSink;
    public string? ProjectOf(Profile? profile) => profile?.Key;
    public string? FallbackProject => Inbox;
    public string Filed(string project) => "Saved as an idea";
    public Task<string> FileAsync(IdeaDraft idea, CancellationToken ct) => Task.FromResult("");
}
