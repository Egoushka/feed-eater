using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Issues;

/// <summary>An idea ready to go to a sink: the project it belongs to, a short title and the full text as Markdown.</summary>
public sealed record IdeaDraft(string Project, string Title, string Body, long? ItemId = null, string Work = "feature");

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

/// <summary>Runs the wheelhouse CLI; a seam so tests need no daemon.</summary>
public interface IBoardRunner
{
    /// <summary>Runs <c>wh</c> with <paramref name="args"/> and returns its exit code and standard output.</summary>
    Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}

public sealed class WhProcessRunner(IOptions<FeedEaterOptions> options) : IBoardRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var board = options.Value.Board;
        var info = new System.Diagnostics.ProcessStartInfo(board.Command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        if (board.KeyFile.Length > 0)
        {
            info.Environment["WHEELHOUSE_SESSIONS_KEY_FILE"] = board.KeyFile;
        }

        if (board.Url.Length > 0)
        {
            info.Environment["WHEELHOUSE_URL"] = board.Url;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("could not start " + board.Command);
            var err = process.StandardError.ReadToEndAsync(ct);
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, process.ExitCode == 0 ? output : await err);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException($"the wheelhouse CLI is not available ({board.Command}): {e.Message}", e);
        }
    }
}

/// <summary>The wheelhouse board: an idea becomes a task item (work feature or research) with source <c>feed:&lt;item id&gt;</c>. GitHub issues stay for kind fix.</summary>
public sealed class BoardIdeaSink(IBoardRunner runner) : IIdeaSink
{
    public string Name => IdeasOptions.BoardSink;
    public string? ProjectOf(Profile? profile) => profile?.Key;
    public string? FallbackProject => LocalIdeaSink.Inbox;
    public string Filed(string project) => $"Added to the board ({project})";

    public async Task<int?> FileAsync(IdeaDraft idea, CancellationToken ct)
    {
        List<string> args = ["board", "add", "--kind", "task", "--work", idea.Work is "research" ? "research" : "feature", "--title", $"[{idea.Project}] {idea.Title}", "--ask", idea.Body];
        if (idea.ItemId is { } id)
        {
            args.AddRange(["--source", $"feed:{id}"]);
        }

        var (code, output) = await runner.RunAsync(args, ct);
        return code == 0 ? null : throw new InvalidOperationException($"wh board add failed: {output.Trim()}");
    }
}
