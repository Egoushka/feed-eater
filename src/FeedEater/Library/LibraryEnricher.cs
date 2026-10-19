using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Sources;
using FeedEater.Storage;

namespace FeedEater.Library;

/// <summary>
/// Gives saved items their full text, a read and an embedding, a few per pass, oldest save first. A save never waits on this: a step
/// that fails is logged and counted as an attempt, and the item is retried on a later pass until <see cref="LibraryStore.MaxEnrichAttempts"/>.
/// </summary>
public sealed class LibraryEnricher(
    LibraryStore library, ItemStore items, ItemText itemText, ItemReader reader, AnalysisStore analysis, LiteLlmClient llm,
    IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<LibraryEnricher> logger)
    : PollingLoop(health, time, logger)
{
    public const string LoopName = "library";
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(30);

    private TimeSpan interval = BusyInterval;   // never zero: the failure backoff multiplies it

    protected override string Name => LoopName;
    protected override TimeSpan Interval => interval;
    internal TimeSpan Wait => interval;

    protected override async Task PollAsync(CancellationToken ct)
    {
        var worked = await RunAsync(ct);
        interval = worked > 0 ? BusyInterval : options.Value.Library.EnrichIdle;
    }

    /// <summary>One pass; returns how many items it handled.</summary>
    internal async Task<int> RunAsync(CancellationToken ct)
    {
        var pending = await library.PendingEnrichmentAsync(options.Value.Library.EnrichBatch, ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        foreach (var id in pending)
        {
            try
            {
                var (done, fullText) = await EnrichAsync(id, ct);
                await library.RecordAttemptAsync(id, done, fullText, ct);
            }
            catch (Exception ex) when (IsOutage(ex, ct))
            {
                // The model or its budget, not the item, is the problem: no attempt is spent, and the loop waits.
                logger.LogWarning(ex, "Enrichment paused: the model is unavailable");
                return 0;
            }
        }

        return pending.Count;
    }

    // Server-side or transport trouble stops the pass; any other 4xx answer is about this one item and counts as its failed attempt,
    // or the oldest item would head the queue for ever (DigestRun treats it the same way).
    private static bool IsOutage(Exception ex, CancellationToken ct) =>
        ex is BudgetExceededException
        || ex is HttpRequestException
        {
            StatusCode: null or >= System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests
        }
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    /// <summary>Runs the steps independently so one failing step does not starve the others; Done is true when all are in place; FullText when the page was really fetched.</summary>
    private async Task<(bool Done, bool FullText)> EnrichAsync(long itemId, CancellationToken ct)
    {
        string? text = null;
        var fetched = true;
        var ok = await StepAsync(itemId, "full text", async () =>
        {
            var full = await itemText.FetchFullTextAsync(itemId, ct);
            text = full?.Text;
            fetched = full?.Fetched ?? true;
        }, outageStops: false, ct);
        if (ok && text is null)
        {
            return (true, false);   // the item is gone; nothing to enrich
        }

        // A read or embedding made on the feed's snippet would be stored for good, so they wait for the page,
        // except on the last attempt: an unfetchable page still gets a summary of what exists.
        if (!ok || !fetched)
        {
            if (await library.EnrichAttemptsAsync(itemId, ct) + 1 < LibraryStore.MaxEnrichAttempts)
            {
                return (false, ok && fetched);
            }
        }

        ok &= fetched;
        ok &= await StepAsync(itemId, "read", async () =>
        {
            if (await analysis.GetReadAsync(itemId, ct) is null
                && await items.GetCandidateAsync(itemId, ct) is { } c
                && await reader.ReadAsync(c, text ?? c.Content, await reader.ContextAsync(ct), projectKey: null, facts: null, linked: null, ct) is null)
            {
                throw new InvalidOperationException("the model returned no usable read");
            }
        }, outageStops: true, ct);

        ok &= await StepAsync(itemId, "embedding", async () =>
        {
            if (await items.UnembeddedAsync(itemId, ct) is { } p)
            {
                var vectors = await llm.EmbedAsync([Ingestor.EmbedText(p.Title, p.Content, p.Url, options.Value.Caps.EmbedChars)], "embed", ct);
                await items.SetEmbeddingsAsync([(itemId, vectors[0])], ct);
            }
        }, outageStops: true, ct);

        return (ok, fetched && text is not null);
    }

    private async Task<bool> StepAsync(long itemId, string step, Func<Task> run, bool outageStops, CancellationToken ct)
    {
        try
        {
            await run();
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !(outageStops && IsOutage(ex, ct)))
        {
            logger.LogWarning(ex, "Enriching saved item {Item}: {Step} failed; it is retried on a later pass", itemId, step);
            return false;
        }
    }
}
