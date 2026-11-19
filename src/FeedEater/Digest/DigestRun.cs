using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Approvals;
using FeedEater.Inbox;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Profiles;
using FeedEater.Ranking;
using FeedEater.Signals;
using FeedEater.Sources;
using FeedEater.Storage;
using FeedEater.Push;
using FeedEater.Text;

namespace FeedEater.Digest;

public sealed record ForceOutcome(bool Sent, string Text);

public sealed record Selection(int Candidates, int Triaged, IReadOnlyList<long> ItemIds, IReadOnlyList<string> Notes);

/// <summary>
/// One digest: rank candidates, triage the top N with the small model, read the top M with the strong one, store it for the
/// stack and the web UI, and push one ntfy message. Every step is stored, so a rerun resumes: results per item in triage/reads.
/// </summary>
public sealed class DigestRun(
    ItemStore items, ReleaseStore releaseStore, ProfileStore profiles, FeedbackStore feedback, AnalysisStore analysis, DigestStore digests,
    LiteLlmClient llm, ItemText itemText, GitHubStarsClient github, PushNotifier push, IInbox inbox, InboxStore inboxStore, LoopHealth health, TasteSwitch tasteSwitch,
    IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<DigestRun> logger, FeedPoller? feeds = null, ApprovalsClient? approvals = null)
{
    private const int TriageMaxTokens = 200;

    // Stateless; built here so the constructor, and every caller of it, stays as it was.
    private readonly ItemReader reader = new(analysis, profiles, llm, options);

    public async Task RunAsync(string date, CancellationToken ct)
    {
        await digests.CloseStaleAsync(date, ct);
        var digest = await digests.GetAsync(date, ct);
        if (digest?.Status == "sent" || (digest?.Status == "failed" && digest.ItemIds.Length == 0))
        {
            return;
        }

        if (digest is null || digest.ItemIds.Length == 0)
        {
            await digests.CreateAsync(date, ct);
            var selection = await SelectAsync(ct);
            if (selection.ItemIds.Count == 0)
            {
                var why = selection.Notes.Count > 0 ? string.Join("; ", selection.Notes) : $"none of {selection.Candidates} new items passed triage";
                await inbox.AddAsync(new InboxEntry($"notice:digest:{date}:none", InboxKinds.Notice, Payload: new JsonObject { ["text"] = $"No digest today: {why}" },
                    ExpiresAt: time.GetUtcNow().AddDays(2)), ct);
                await digests.MarkFailedAsync(date, why, ct);
                return;
            }

            await digests.SetSelectionAsync(date, selection.Candidates, selection.Triaged, [.. selection.ItemIds],
                selection.Notes.Count == 0 ? null : string.Join('\n', selection.Notes), ct);
            if (selection.Notes.Count > 0)
            {
                // The stack has no header, so the notes are stored as a card.
                await inboxStore.AddAsync(new InboxEntry($"notice:digest:{date}:notes", InboxKinds.Notice,
                    Payload: new JsonObject { ["text"] = string.Join('\n', selection.Notes) }, ExpiresAt: time.GetUtcNow().AddDays(2)), time.GetUtcNow(), ct);
            }

            digest = (await digests.GetAsync(date, ct))!;
        }

        await SendAsync(digest, ct);
    }

    /// <summary>
    /// A digest on demand. A digest that was already sent is not sent again unless <paramref name="resend"/>; one that never
    /// went out is built (or resumed) now. Returns what happened, in words.
    /// </summary>
    public async Task<ForceOutcome> ForceAsync(string date, bool resend, CancellationToken ct)
    {
        var digest = await digests.GetAsync(date, ct);
        if (digest?.Status == "sent")
        {
            if (!resend)
            {
                return new ForceOutcome(false, "Today's digest was already sent; nothing sent again.");
            }

            await digests.ResetSentAsync(date, ct);
            await SendAsync(digest with { SentCount = 0, SentMessageIds = [] }, ct);
            return new ForceOutcome(true, "Sent again.");
        }

        await digests.ReopenAsync(date, ct);
        await RunAsync(date, ct);
        var after = await digests.GetAsync(date, ct);
        return after?.Status == "sent"
            ? new ForceOutcome(true, "Sent.")
            : new ForceOutcome(false, $"No digest: {after?.Error ?? "nothing to send"}");
    }

    internal async Task<Selection> SelectAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var notes = new List<string>();
        if (health.IsDown(Ingestor.LoopName))
        {
            notes.Add("Miniflux was unreachable at the last poll; some items may be missing");
        }

        if (feeds is not null)
        {
            notes.AddRange(await feeds.NotesAsync(ct));
        }

        if (health.IsDown(Ingestor.EmbedName))
        {
            notes.Add("Embeddings were unavailable at the last poll; new items may be missing");
        }

        var zone = o.Zone;
        var localDay = TimeZoneInfo.ConvertTime(now, zone).Date;
        var dayStart = new DateTimeOffset(localDay, zone.GetUtcOffset(localDay));
        var floor = now - TimeSpan.FromDays(o.Caps.CandidateDays);
        var candidates = await items.CandidatesAsync(floor, dayStart, ct);
        var readContext = await reader.ContextAsync(ct);
        var profileList = readContext.Profiles;
        var taste = Taste.Build(
            await feedback.PositiveWeightedAsync(o.Caps.Centroid, ct), await feedback.NegativeWeightedAsync(o.Caps.Centroid, ct),
            await feedback.FeedVotesAsync(ct), o.Weights);
        var learnedOn = await tasteSwitch.IsOnAsync(ct);
        if (learnedOn)
        {
            var (model, note) = LearnedTaste.Prepare(await feedback.LabeledVectorsAsync(ct), o.Taste);
            taste = taste with { Learned = model };
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        var scored = candidates.Select(c => Scorer.Score(c.Id, c.FeedId, c.Embedding, profileList, taste, o.Weights)).ToList();
        await items.SetScoresAsync(scored, ct, RankerId.Compute(o.Weights, learnedOn, profileList));

        var byId = candidates.ToDictionary(c => c.Id);
        var keys = readContext.Keys;
        if (readContext.IsExample)
        {
            notes.Add(ProfileFile.ExampleNote(o.ProfilePath));
        }

        var about = readContext.About;

        var budgetSpent = false;
        var rateLimited = false;
        var triaged = new List<(Scored Score, TriageResult Triage)>();
        int triageCalls = 0, triageTransportFailures = 0;
        foreach (var s in scored.OrderByDescending(s => s.Score).Take(o.Caps.Triage))
        {
            var t = await analysis.GetTriageAsync(s.ItemId, ct);
            if (t is null)
            {
                if (budgetSpent)
                {
                    break;
                }

                var c = byId[s.ItemId];
                var (system, user) = Prompts.Triage(about, profileList, c.Title, c.FeedTitle, c.Content, o.Caps.TriageChars, Clip(c.ExtraText, 1200));
                triageCalls++;
                try
                {
                    var chat = await llm.ChatAsync(o.Llm.TriageModel, system, user, TriageMaxTokens, "triage", ct);
                    if (chat.RateLimited && !rateLimited)
                    {
                        notes.Add("waited for the token limit to reset");
                        rateLimited = true;
                    }

                    t = LlmJson.Triage(chat.Content, keys);
                    await analysis.SaveTriageAsync(s.ItemId, t, o.Llm.TriageModel, ct);
                }
                catch (BudgetExceededException)
                {
                    notes.Add("LLM budget reached during triage");
                    budgetSpent = true;
                    triageCalls--;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    logger.LogWarning(ex, "Triage of item {Item} failed; counted as marginal", s.ItemId);
                    triageTransportFailures++;
                    t = new TriageResult { Relevance = 1, Reason = "triage failed" };
                }
            }

            triaged.Add((s, t));
        }

        if (triageCalls > 0 && triageTransportFailures == triageCalls && !budgetSpent)
        {
            throw new InvalidOperationException($"All {triageCalls} triage calls failed; the LLM looks unreachable");
        }

        var picked = triaged
            .Where(x => x.Triage.Relevance >= o.Caps.MinRelevance)
            .OrderByDescending(x => x.Triage.Relevance).ThenByDescending(x => x.Score.Score)
            .ToList();

        var repoFacts = new Dictionary<(string, string), string?>();
        var read = new List<(Scored Score, TriageResult Triage, ReadResult Read)>();
        int readCalls = 0, readTransportFailures = 0, readsCached = 0;
        foreach (var (s, t) in picked)
        {
            // Items the reader model vetoes after reading are skipped and the next candidate takes the slot, up to a bounded extra spend.
            if (read.Count >= o.Caps.Read || readCalls >= o.Caps.Read * 2)
            {
                break;
            }

            var r = await analysis.GetReadAsync(s.ItemId, ct);
            if (r is not null)
            {
                readsCached++;
            }

            if (r is null && !budgetSpent)
            {
                readCalls++;
                var c = byId[s.ItemId];
                try
                {
                    var text = await itemText.FullTextAsync(c, ct);
                    var facts = await RepoFactsAsync(repoFacts, c.Url, text, ct);
                    r = await reader.ReadAsync(c, text, readContext, t.Project ?? s.ProfileKey, facts, Clip(c.ExtraText, o.Fetch.PageChars), ct);
                }
                catch (BudgetExceededException)
                {
                    notes.Add("LLM budget reached; fewer highlights today");
                    budgetSpent = true;
                    readCalls--;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    logger.LogWarning(ex, "Reading item {Item} failed; left out of the digest", s.ItemId);
                    readTransportFailures++;
                }
            }

            if (r is not null && (r.Relevance ?? o.Caps.MinRelevance) >= o.Caps.MinReadRelevance)
            {
                read.Add((s, t, r));
            }
        }

        if (readCalls > 0 && readTransportFailures == readCalls && readsCached == 0 && !budgetSpent)
        {
            throw new InvalidOperationException($"All {readCalls} reads failed; the LLM looks unreachable");
        }

        var readsDone = readCalls - readTransportFailures + readsCached;
        if (read.Count == 0 && readsDone > 0)
        {
            notes.Add($"the reader model rated all {readsDone} items it read below {o.Caps.MinReadRelevance}");
        }

        // Items the reader called marginal fill the slots left after the ones it called worth reading.
        var ordered = read
            .OrderBy(x => (x.Read.Relevance ?? o.Caps.MinRelevance) < o.Caps.MinRelevance ? 1 : 0)
            .ThenBy(x => x.Read.Suggestion is null ? 1 : 0)
            .ThenByDescending(x => x.Triage.Relevance)
            .ThenByDescending(x => x.Score.Score)
            .Select(x => x.Score.ItemId)
            .ToList();
        return new Selection(candidates.Count, triaged.Count, ordered, notes);
    }

    /// <summary>One line of GitHub facts for a repo the item links, fetched once per repo per run; null when there is no link or GitHub does not answer.</summary>
    private async Task<string?> RepoFactsAsync(Dictionary<(string, string), string?> cache, string url, string text, CancellationToken ct)
    {
        if (GitHubRepos.Find(url, text) is not var (owner, repo))
        {
            return null;
        }

        var key = (owner.ToLowerInvariant(), repo.ToLowerInvariant());
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? line = null;
        try
        {
            line = (await github.RepoFactsAsync(owner, repo, ct))?.Line();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "GitHub facts for {Owner}/{Repo} unavailable", owner, repo);
        }

        if (line is null)
        {
            logger.LogDebug("No GitHub facts for {Owner}/{Repo}", owner, repo);
        }

        cache[key] = line;
        return line;
    }

    private static string ReleaseLine(ReleaseRow r) =>
        $"{r.Repo} {r.Version} is out (breaking: {r.Breaking ?? "unknown"}){(r.Summary is null ? "" : ": " + r.Summary)}";

    private static string? Clip(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    private async Task SendAsync(DigestRow digest, CancellationToken ct)
    {
        // Delivered to the stack, which reads the digest's items itself.
        await digests.SetSentCountAsync(digest.LocalDate, digest.ItemIds.Length, ct);
        await digests.MarkSentAsync(digest.LocalDate, time.GetUtcNow(), ct);
        await releaseStore.TakeForDigestAsync(digest.LocalDate, ct);
        await NotifyApprovalsAsync(digest, ct);
        await push.NotifyDigestAsync(digest.LocalDate, digest.ItemIds.Length, ct);
    }

    /// <summary>One card in the approvals inbox, after the digest is marked sent (the stack already holds it); the same id on a resend rewrites the card.</summary>
    private async Task NotifyApprovalsAsync(DigestRow digest, CancellationToken ct)
    {
        if (approvals is not { Enabled: true })
        {
            return;
        }

        var shown = await items.DigestItemsAsync(digest.ItemIds, ct);
        var byProject = string.Join(", ", shown
            .GroupBy(v => v.Project ?? "other")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}"));
        var text = string.Join('\n', new[] { byProject }.Concat(digest.Note is null ? [] : digest.Note.Split('\n')));
        await approvals.NotifyAsync(new ApprovalsNote(
            $"{ApprovalsClient.Source}:digest:{digest.LocalDate}", $"Digest {digest.LocalDate}: {shown.Count} items", byProject, text,
            time.GetUtcNow().AddDays(2)), ct);
    }
}
