using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Inbox;
using FeedEater.Issues;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Text;

namespace FeedEater.WeeklyIdeas;

/// <summary>
/// The shortlist run behind <see cref="WeeklyIdeasJob"/> and <see cref="DailyIdeasJob"/>: what was liked, saved, bookmarked or marked improve
/// inside the run's window becomes a ranked shortlist. Each idea waits in the inbox for Keep or Drop (Keep files it as a GitHub issue labelled
/// triage in its project's repo); the top few go to Telegram. An idea whose source was filed before, or that repeats an issue or a backlog row,
/// is left out. A run with no leads asks the model nothing and sends nothing. Quiet hours hold it.
/// </summary>
public abstract class IdeasJob(
    WeeklyIdeaStore store, IPeerGapSource? gapSource, ProfileStore profiles, GitHubIssues issues, BacklogClient backlog, LiteLlmClient llm, TelegramClient telegram, InboxStore inboxStore, QuietHours quiet,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private const int MaxLeads = 60;
    private const int MaxIssueTitles = 100;
    private const int MaxTokens = 2000;

    /// <summary>How far back the run looks for leads.</summary>
    protected abstract TimeSpan Window { get; }

    /// <summary>Most ideas staged per run.</summary>
    protected abstract int MaxIdeas { get; }

    /// <summary>The span the model is told it ranks ("week", "day").</summary>
    protected abstract string Span { get; }

    /// <summary>The run's label from its key (2026-W43, 2026-10-19): the payload's week, the Telegram heading and the issue's source line.</summary>
    protected abstract string Label(string key);

    /// <summary>True to leave out sources already staged as ideas, kept, dropped or open, so a run offers only new ones.</summary>
    protected virtual bool OnlyUnstaged => false;

    protected override async Task<bool> HoldAsync(CancellationToken ct) => await quiet.IsQuietAsync(ct);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var settings = Settings.WeeklyIdeas;
        var since = Time.GetUtcNow() - Window;
        var filed = await store.FiledUrlsAsync(ct);
        if (OnlyUnstaged)
        {
            filed = new HashSet<string>(filed.Concat(await store.StagedUrlsAsync(ct)), StringComparer.Ordinal);
        }

        var week = Label(key);
        var leads = await LeadsAsync(since, filed, ct);
        var gapUrls = leads.Where(PeerGapLeads.IsGap).Select(l => l.Url).ToList();
        if (gapUrls.Count > 0)
        {
            Logger.LogInformation("Peer-gap candidates offered to the ranking ({Count}): {Urls}", gapUrls.Count, string.Join(", ", gapUrls));
        }

        if (leads.Count == 0)
        {
            Logger.LogInformation("No leads this {Span}; no shortlist", Span);
            return;
        }

        var targets = (await profiles.AllAsync(ct)).Where(p => p.Repo is { Length: > 0 })
            .Select(p => new Target(p.Repo!, p.Key, p.Description)).DistinctBy(t => t.Repo, StringComparer.OrdinalIgnoreCase).ToList();
        var fallback = Settings.GitHub.FallbackRepo;
        if (fallback.Length > 0 && targets.All(t => !string.Equals(t.Repo, fallback, StringComparison.OrdinalIgnoreCase)))
        {
            targets.Add(new Target(fallback, "other", "Ideas that fit no other project."));
        }

        if (targets.Count == 0)
        {
            Logger.LogWarning("No repo to file ideas in (no profile has a repo and GitHub:FallbackRepo is empty)");
            return;
        }

        var issueTitles = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                issueTitles.AddRange((await issues.RecentTitlesAsync(t.Repo, ct)).Take(MaxIssueTitles).Select(title => $"{t.Repo}: {title}"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden })
            {
                Logger.LogWarning(ex, "Repo {Repo} unavailable; its issues are not checked", t.Repo);
            }
        }

        // A configured backlog that cannot be read fails the run: filing without it would repeat rows.
        var backlogText = await backlog.TextAsync(ct);
        var reply = await llm.ChatAsync(Settings.Llm.ReadModel, IdeaShortlist.System(MaxIdeas, Span),
            IdeaShortlist.User(leads, targets, issueTitles, await store.RecentTitlesAsync(50, ct), backlogText), MaxTokens, Name, ct);
        await store.MarkGapsOfferedAsync(gapUrls, week, ct);   // offered once, whether or not the ranking keeps it
        var entries = IdeaShortlist.Parse(reply.Content, leads.Count, targets.Select(t => t.Repo).ToList(), fallback.Length > 0 ? fallback : targets[0].Repo)
            .Select(e => leads[e.Lead].Project is { } fixedRepo ? e with { Project = fixedRepo } : e)
            .Take(MaxIdeas).ToList();
        if (entries.Count == 0)
        {
            Logger.LogInformation("The model found no idea among {Count} leads: {Reply}", leads.Count, reply.Content.Length <= 600 ? reply.Content : reply.Content[..600]);
            return;
        }

        var staged = new List<(ShortlistEntry Entry, Lead Lead, long InboxId)>();
        foreach (var entry in entries)
        {
            var lead = leads[entry.Lead];
            var title = entry.Idea.Length <= 80 ? entry.Idea : entry.Idea[..79] + "…";
            var url = UrlCanonicalizer.Canonical(lead.Url);
            var id = await inboxStore.AddAsync(new InboxEntry($"idea:{url}", InboxKinds.Idea,
                Payload: new JsonObject
                {
                    ["week"] = week,
                    ["title"] = title,
                    ["project"] = entry.Project,
                    ["body"] = Body(entry, lead, week),
                    ["url"] = url,
                    ["sourceTitle"] = lead.Title,
                    ["signal"] = lead.Signal,
                    ["why"] = entry.Why,
                    ["where"] = entry.Where,
                    ["effort"] = entry.Effort.ToString(),
                },
                ExpiresAt: Time.GetUtcNow().AddDays(14)), Time.GetUtcNow(), ct);
            staged.Add((entry, lead, id));
        }

        Logger.LogInformation("Staged {Count} ideas for {Week}", staged.Count, week);
        var stagedGaps = staged.Where(s => PeerGapLeads.IsGap(s.Lead)).Select(s => s.Lead.Url).ToList();
        if (stagedGaps.Count > 0)
        {
            Logger.LogInformation("Peer-gap ideas staged ({Count}): {Urls}", stagedGaps.Count, string.Join(", ", stagedGaps));
        }

        if (Settings.Telegram.Token.Length > 0)
        {
            await telegram.SendAsync(Settings.Telegram.AllowedUserId, Message(week, staged, settings.TopInMessage), ct);
        }
    }

    private async Task<List<Lead>> LeadsAsync(DateTimeOffset since, IReadOnlySet<string> filed, CancellationToken ct)
    {
        var leads = new List<Lead>();
        foreach (var c in await store.CandidatesAsync(since, MaxLeads, ct))
        {
            leads.Add(new Lead(c.Signal, c.Title, c.Url, c.Suggestion ?? c.Summary ?? c.Why ?? ""));
        }

        foreach (var b in await store.BookmarksAsync(since, MaxLeads, ct))
        {
            leads.Add(new Lead("bookmarked", b.Title, b.Url!, ""));
        }

        foreach (var n in await store.NotesAsync(since, MaxLeads, ct))
        {
            leads.Add(new Lead(ResearchNotesImporter.Signal, n.Title, n.Url, n.Content));
        }

        IReadOnlyList<Lead> gaps = gapSource is null ? [] : await gapSource.LeadsAsync(new HashSet<string>(filed.Concat(await store.OfferedGapUrlsAsync(ct)), StringComparer.Ordinal), ct);
        var seen = new HashSet<string>(filed, StringComparer.Ordinal);
        var gapLeads = gaps.Take(PeerGapLeads.PerSet).Where(l => seen.Add(UrlCanonicalizer.Canonical(l.Url))).ToList();
        return leads.Where(l => seen.Add(UrlCanonicalizer.Canonical(l.Url))).Take(MaxLeads - gapLeads.Count).Concat(gapLeads).ToList();
    }

    /// <summary>The issue body, in Markdown.</summary>
    internal static string Body(ShortlistEntry e, Lead lead, string week)
    {
        var body = new StringBuilder(Markdown.Text(e.Idea));
        if (e.Why.Length > 0)
        {
            body.Append($"\n\nWhy: {Markdown.Text(e.Why)}");
        }

        body.Append($"\n\nWhere: {Markdown.Text(e.Where.Length > 0 ? e.Where : e.Project)} · effort {e.Effort}");
        if (lead.Extra is { Length: > 0 })
        {
            body.Append("\n\n").Append(lead.Extra);
        }

        body.Append(PeerGapLeads.IsGap(lead)
            ? $"\n\nSource: {Markdown.Text(lead.Title)} ({Markdown.Text(lead.Signal)}, feed-eater ideas {week})"
            : $"\n\nSource: {Markdown.Link(lead.Title, lead.Url)} ({Markdown.Text(lead.Signal)}, feed-eater ideas {week})");
        return body.ToString();
    }

    internal static OutMessage Message(string week, IReadOnlyList<(ShortlistEntry Entry, Lead Lead, long InboxId)> staged, int detailed)
    {
        var html = new StringBuilder($"💡 <b>Ideas for {week}</b> ({staged.Count} waiting for Keep or Drop)\n");
        for (var i = 0; i < staged.Count; i++)
        {
            var (e, lead, _) = staged[i];
            if (i >= detailed)
            {
                html.Append($"\n{i + 1}. {E(e.Idea)}");
                continue;
            }

            html.Append($"\n{i + 1}. <b>{E(e.Idea)}</b>\n   {E(e.Project)} · {E(e.Where)} · {e.Effort}");
            if (e.Why.Length > 0)
            {
                html.Append($"\n   {E(e.Why)}");
            }

            html.Append(PeerGapLeads.IsGap(lead) ? $"\n   {E(lead.Title)} (peer gap)\n" : $"\n   <a href=\"{E(lead.Url)}\">{E(lead.Title)}</a>\n");
        }

        var keyboard = staged.Select((t, i) => (IReadOnlyList<Button>)
            [new Button($"✅ {i + 1}", CallbackData.KeepIdea(t.InboxId)), new Button($"✖ {i + 1}", CallbackData.DropIdea(t.InboxId))]).ToList();
        return new OutMessage(html.ToString(), keyboard);
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
