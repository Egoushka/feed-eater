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

    private TimeSpan interval = TimeSpan.Zero;

    protected override string Name => LoopName;
    protected override TimeSpan Interval => interval;

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
            await library.RecordAttemptAsync(id, await EnrichAsync(id, ct), ct);
        }

        return pending.Count;
    }

    /// <summary>Runs the three steps independently so one failing step does not starve the others; true when all are in place.</summary>
    private async Task<bool> EnrichAsync(long itemId, CancellationToken ct)
    {
        string? text = null;
        var ok = await StepAsync(itemId, "full text", async () => text = await itemText.FullTextAsync(itemId, ct), ct);
        if (ok && text is null)
        {
            return true;   // the item is gone; nothing to enrich
        }

        ok &= await StepAsync(itemId, "read", async () =>
        {
            if (await analysis.GetReadAsync(itemId, ct) is null
                && await items.GetCandidateAsync(itemId, ct) is { } c
                && await reader.ReadAsync(c, text ?? c.Content, await reader.ContextAsync(ct), projectKey: null, facts: null, linked: null, ct) is null)
            {
                throw new InvalidOperationException("the model returned no usable read");
            }
        }, ct);

        ok &= await StepAsync(itemId, "embedding", async () =>
        {
            if (await items.UnembeddedAsync(itemId, ct) is { } p)
            {
                var vectors = await llm.EmbedAsync([Ingestor.EmbedText(p.Title, p.Content, p.Url, options.Value.Caps.EmbedChars)], "embed", ct);
                await items.SetEmbeddingsAsync([(itemId, vectors[0])], ct);
            }
        }, ct);

        return ok;
    }

    private async Task<bool> StepAsync(long itemId, string step, Func<Task> run, CancellationToken ct)
    {
        try
        {
            await run();
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Enriching saved item {Item}: {Step} failed; it is retried on a later pass", itemId, step);
            return false;
        }
    }
}
