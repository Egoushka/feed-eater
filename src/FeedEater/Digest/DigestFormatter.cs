using System.Globalization;
using FeedEater.Storage;

namespace FeedEater.Digest;

public sealed record DigestItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public string? Project { get; init; }
    public string Kind { get; init; } = "fyi";
    public string Summary { get; init; } = "";
    public string Why { get; init; } = "";
    public string? Suggestion { get; init; }
    /// <summary>The stored <c>RankSignals</c> as JSON, null for items scored before they were kept.</summary>
    public string? RankSignals { get; init; }
    public string Ranked => RankSignals is null ? "" : System.Text.Json.JsonSerializer.Deserialize<FeedEater.Ranking.RankSignals>(RankSignals)?.Line() ?? "";
    public IReadOnlyList<ClusterMember> AlsoIn { get; init; } = [];
}

/// <summary>Which optional buttons a surface offers: the filed-idea label says where the idea went.</summary>
public sealed record ButtonStyle(bool CanSave, bool ToGitHub)
{
    public static readonly ButtonStyle Default = new(true, true);

    public static ButtonStyle From(FeedEaterOptions o) => new(true, o.IdeaSink == IdeasOptions.GitHubSink);
}

/// <summary>Text helpers for titles and links.</summary>
public static class DigestFormatter
{
    private const int MaxLinkedUrl = 1000;

    internal static bool IsLinkable(string url) =>
        url.Length <= MaxLinkedUrl && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    internal static string Clip(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }
}
