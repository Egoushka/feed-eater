using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Issues;

/// <summary>An idea ready to go to a sink: the project it belongs to, a short title and the full text as Markdown.</summary>
public sealed record IdeaDraft(string Project, string Title, string Body);

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

    /// <summary>Stores the idea and returns the sink's number for it (null when the sink keeps none).</summary>
    Task<int?> FileAsync(IdeaDraft idea, CancellationToken ct);
}

/// <summary>GitHub: an idea becomes an issue labelled triage in the profile's repo.</summary>
public sealed class GitHubIdeaSink(GitHubIssues issues, IOptions<FeedEaterOptions> options) : IIdeaSink
{
    public string Name => IdeasOptions.GitHubSink;
    public string? ProjectOf(Profile? profile) => profile?.Repo;
    public string? FallbackProject => options.Value.GitHub.FallbackRepo is { Length: > 0 } fallback ? fallback : null;
    public string Filed(string project) => $"Filed in {project}";

    public async Task<int?> FileAsync(IdeaDraft idea, CancellationToken ct) => options.Value.GitHub.IssuesEnabled
        ? await issues.CreateAsync(idea.Project, idea.Title, idea.Body, ct)
        : throw new InvalidOperationException("GitHub issues are not configured (GitHub:Token and GitHub:FallbackRepo)");
}

/// <summary>The default without GitHub issues: the idea stays in the archive's own list (/ui/ideas, the feed_ideas MCP tool).</summary>
public sealed class LocalIdeaSink : IIdeaSink
{
    public const string Inbox = "inbox";

    public string Name => IdeasOptions.LocalSink;
    public string? ProjectOf(Profile? profile) => profile?.Key;
    public string? FallbackProject => Inbox;
    public string Filed(string project) => "Saved as an idea";
    public Task<int?> FileAsync(IdeaDraft idea, CancellationToken ct) => Task.FromResult<int?>(null);
}
