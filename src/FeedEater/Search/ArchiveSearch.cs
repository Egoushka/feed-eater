using FeedEater.Llm;
using FeedEater.Storage;

namespace FeedEater.Search;

/// <summary>Hybrid search for the MCP tool and the web UI: embeds the query, or falls back to keywords when the embedder is slow or down.</summary>
public sealed class ArchiveSearch(ItemStore items, LiteLlmClient llm)
{
    /// <summary>How long a search waits for the query embedding before it falls back to keywords.</summary>
    internal static TimeSpan EmbedTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, string? project, string? kind, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct)
    {
        float[]? vector = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(EmbedTimeout);
        try
        {
            vector = (await llm.EmbedAsync([query], "search", timeout.Token))[0];
        }
        catch (Exception ex) when (ex is HttpRequestException or BudgetExceededException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Keyword search still answers.
        }

        return await items.SearchAsync(vector, query, project, kind, from, to, limit, ct);
    }
}
