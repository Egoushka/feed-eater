using System.Net;
using System.Text;

namespace FeedEater.Sources;

/// <summary>Telegram <c>/feeds</c> (list) and <c>/feeds add url</c>. Returns Telegram HTML; every variable part is encoded here.</summary>
public sealed class FeedsCommand(FeedStore feeds, FeedManager manager)
{
    public const string Usage = "Usage: /feeds lists your feeds, /feeds add &lt;url&gt; subscribes (a site address works too).";

    private const int MaxChars = 3500;

    public async Task<string> RunAsync(string? argument, CancellationToken ct)
    {
        if (argument is null)
        {
            return await ListAsync(ct);
        }

        var words = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words is not ["add", var url])
        {
            return Usage;
        }

        var result = await manager.AddAsync(url, ct);
        return result switch
        {
            { Ok: false } => $"Not added: {E(result.Error!)}",
            { Existing: true } => $"Already subscribed: {E(result.Title)}.",
            _ => $"Added {E(result.Title)}; {result.Items} recent entries stored.",
        };
    }

    private async Task<string> ListAsync(CancellationToken ct)
    {
        var all = await feeds.ListAsync(ct);
        if (all.Count == 0)
        {
            return "No feeds yet. /feeds add &lt;url&gt;, or import an OPML file on /ui/sources.";
        }

        var text = new StringBuilder($"{all.Count} feeds\n");
        var shown = 0;
        foreach (var f in all)
        {
            var line = $"\n{E(f.Title)}{(f.FailCount > 0 ? $" ⚠️ {E(f.LastError ?? "failing")}" : "")}";
            if (text.Length + line.Length > MaxChars)
            {
                break;
            }

            text.Append(line);
            shown++;
        }

        return shown < all.Count ? text.Append($"\n\nand {all.Count - shown} more on /ui/sources").ToString() : text.ToString();
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
