using FeedEater.Inbox;
using FeedEater.Issues;
using FeedEater.Storage;

namespace FeedEater.WeeklyIdeas;

public enum IdeaOutcome { Filed, Dropped, AlreadyDecided, NotFound, Failed }

public sealed record IdeaDecision(IdeaOutcome Outcome, string? Project = null, string? Error = null);

/// <summary>Keep files a staged idea as a GitHub issue labelled triage; Drop remembers its source. The inbox row's done_at is the decision, so a second press changes nothing.</summary>
public sealed class IdeaDecisions(InboxStore inbox, WeeklyIdeaStore ideas, GitHubIssues issues, TimeProvider time)
{
    public async Task<IdeaDecision> KeepAsync(long inboxId, CancellationToken ct)
    {
        // Claim first: a concurrent Keep or Drop then sees the row as decided and files nothing.
        if (await ClaimAsync(inboxId, ct) is not { } row)
        {
            return await MissAsync(inboxId, ct);
        }

        var p = row.Payload;
        var repo = Str(p, "project");
        int issue;
        try
        {
            issue = await issues.CreateAsync(repo, Str(p, "title"), Str(p, "body"), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            await inbox.ReopenAsync(inboxId, CancellationToken.None);
            return new(IdeaOutcome.Failed, Error: ex.Message);
        }
        catch
        {
            await inbox.ReopenAsync(inboxId, CancellationToken.None);
            throw;
        }

        await ideas.AddAsync(new FiledIdea(Str(p, "url"), Str(p, "week"), Str(p, "title"), repo, issue), ct);
        return new(IdeaOutcome.Filed, repo);
    }

    public async Task<IdeaDecision> DropAsync(long inboxId, CancellationToken ct)
    {
        if (await ClaimAsync(inboxId, ct) is not { } row)
        {
            return await MissAsync(inboxId, ct);
        }

        var p = row.Payload;
        await ideas.DropAsync(Str(p, "url"), Str(p, "week"), Str(p, "title"), Str(p, "project"), ct);
        return new(IdeaOutcome.Dropped);
    }

    /// <summary>The open idea row, marked done so no second press gets it; null when it is missing, not an idea or already decided.</summary>
    private async Task<InboxRow?> ClaimAsync(long id, CancellationToken ct) =>
        await inbox.GetAsync(id, ct) is { Kind: InboxKinds.Idea, DoneAt: null } row && await inbox.DoneAsync(id, time.GetUtcNow(), ct) ? row : null;

    private async Task<IdeaDecision> MissAsync(long id, CancellationToken ct) =>
        await inbox.GetAsync(id, ct) is { Kind: InboxKinds.Idea } ? new(IdeaOutcome.AlreadyDecided) : new(IdeaOutcome.NotFound);

    private static string Str(System.Text.Json.Nodes.JsonObject p, string key) => p[key]?.GetValue<string>() ?? "";
}
