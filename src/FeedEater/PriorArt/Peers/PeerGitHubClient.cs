using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.PriorArt.Peers;

/// <summary>A repo's default README, cut to <c>PeerHarvest:ReadmeMaxBytes</c>, with its git blob sha (the episode hash).</summary>
public sealed record PeerReadme(string Sha, string Text, string Url);

/// <summary>
/// The GitHub calls the peer harvester needs beyond the stars/last-push/license counters <see cref="FeedEater.Signals.GitHubStarsClient"/>
/// already reads: the dependency graph, the README and the top of the file tree. No clone.
/// </summary>
public sealed class PeerGitHubClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    private const int MaxTreeDirs = 25;

    /// <summary>Null when the repo has no dependency graph (404) or it is not enabled for this repo (403): the caller keeps the snapshot facts only.</summary>
    public async Task<IReadOnlyList<SbomPackage>?> SbomAsync(string owner, string repo, CancellationToken ct)
    {
        using var response = await http.GetAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/dependency-graph/sbom", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return SbomParser.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Null when the repo has no README.</summary>
    public async Task<PeerReadme?> ReadmeAsync(string owner, string repo, CancellationToken ct)
    {
        using var response = await http.GetAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/readme", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        var sha = Json.Str(body, "sha") ?? "";
        var content = Json.Str(body, "content") ?? "";
        var text = Json.Str(body, "encoding") == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "", StringComparison.Ordinal))) : content;
        return new PeerReadme(sha, Cut(text, options.Value.PeerHarvest.ReadmeMaxBytes), $"https://github.com/{owner}/{repo}#readme");
    }

    /// <summary>Paths of the repo root and, for at most <see cref="MaxTreeDirs"/> of its directories, their own entries. Directories end with <c>/</c>.</summary>
    public async Task<IReadOnlyList<string>> TreeAsync(string owner, string repo, CancellationToken ct)
    {
        var paths = new List<string>();
        var dirs = 0;
        foreach (var entry in await ContentsAsync(owner, repo, "", ct))
        {
            paths.Add(Entry(entry));
            if (Json.Str(entry, "type") == "dir" && dirs++ < MaxTreeDirs)
            {
                paths.AddRange((await ContentsAsync(owner, repo, Json.Str(entry, "path") ?? "", ct)).Select(Entry));
            }
        }

        return paths;
    }

    private async Task<List<JsonElement>> ContentsAsync(string owner, string repo, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents/{path}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];   // a dir GitHub refuses (huge, a submodule) is left out; the rest of the tree still helps the card
        }

        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.ValueKind == JsonValueKind.Array ? body.EnumerateArray().ToList() : [];
    }

    private static string Entry(JsonElement e) => (Json.Str(e, "path") ?? "") + (Json.Str(e, "type") == "dir" ? "/" : "");

    /// <summary>At most <paramref name="maxBytes"/> UTF-8 bytes, cut at a character boundary.</summary>
    private static string Cut(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var cut = text.Length > maxBytes ? text[..maxBytes] : text;
        while (cut.Length > 0 && Encoding.UTF8.GetByteCount(cut) > maxBytes)
        {
            cut = cut[..^1];
        }

        return cut;
    }
}
