using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Llm;
using FeedEater.Storage;

namespace FeedEater.Search;

/// <summary>A question about one item, answered by the read model from the item's own text.</summary>
public sealed class ItemAnswer(ItemStore items, LiteLlmClient llm, IOptions<FeedEaterOptions> options)
{
    private const int MaxTokens = 400;
    private const int MaxAnswer = 1500;

    /// <summary>Null when the item does not exist.</summary>
    public async Task<string?> AskAsync(long itemId, string question, CancellationToken ct) =>
        await items.GetAsync(itemId, ct) is { } item ? await AskAsync(item, question, ct) : null;

    public async Task<string> AskAsync(ItemView item, string question, CancellationToken ct)
    {
        var o = options.Value;
        var (system, user) = Prompts.AboutItem(question, item.Title, item.Url, item.Feed, item.Content, o.Caps.ReadChars);
        var reply = await llm.ChatAsync(o.Llm.ReadModel, system, user, MaxTokens, "reply", ct);
        var text = reply.Content.Trim();
        return text.Length <= MaxAnswer ? text : text[..(MaxAnswer - 1)] + "…";
    }
}
