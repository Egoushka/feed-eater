using FeedEater.Inbox;
using FeedEater.Plane;
using FeedEater.Storage;

namespace FeedEater.WeeklyIdeas;

public enum IdeaOutcome { Filed, Dropped, AlreadyDecided, NotFound, Failed }

public sealed record IdeaDecision(IdeaOutcome Outcome, string? Project = null, string? Error = null);

/// <summary>Keep files a staged idea in Plane Intake; Drop remembers its source. The inbox row's done_at is the decision, so a second press changes nothing.</summary>
public sealed class IdeaDecisions(InboxStore inbox, WeeklyIdeaStore ideas, PlaneClient plane, TimeProvider time)
{
    public async Task<IdeaDecision> KeepAsync(long inboxId, CancellationToken ct)
    {
        if (await OpenIdeaAsync(inboxId, ct) is not { } row)
        {
            return await inbox.GetAsync(inboxId, ct) is null ? new(IdeaOutcome.NotFound) : new(IdeaOutcome.AlreadyDecided);
        }

        var p = row.Payload;
        string issue;
        try
        {
            issue = await plane.CreateIntakeAsync(Str(p, "project"), Str(p, "title"), Str(p, "html"), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new(IdeaOutcome.Failed, Error: ex.Message);
        }

        if (!await inbox.DoneAsync(inboxId, time.GetUtcNow(), ct))
        {
            return new(IdeaOutcome.AlreadyDecided);   // a concurrent press won; its Plane item stands
        }

        await ideas.AddAsync(new FiledIdea(Str(p, "url"), Str(p, "week"), Str(p, "title"), Str(p, "project"), issue), ct);
        return new(IdeaOutcome.Filed, Str(p, "project"));
    }

    public async Task<IdeaDecision> DropAsync(long inboxId, CancellationToken ct)
    {
        if (await OpenIdeaAsync(inboxId, ct) is not { } row || !await inbox.DoneAsync(inboxId, time.GetUtcNow(), ct))
        {
            return await inbox.GetAsync(inboxId, ct) is null ? new(IdeaOutcome.NotFound) : new(IdeaOutcome.AlreadyDecided);
        }

        var p = row.Payload;
        await ideas.DropAsync(Str(p, "url"), Str(p, "week"), Str(p, "title"), Str(p, "project"), ct);
        return new(IdeaOutcome.Dropped);
    }

    private async Task<InboxRow?> OpenIdeaAsync(long id, CancellationToken ct) =>
        await inbox.GetAsync(id, ct) is { Kind: InboxKinds.Idea, DoneAt: null } row ? row : null;

    private static string Str(System.Text.Json.Nodes.JsonObject p, string key) => p[key]?.GetValue<string>() ?? "";
}
