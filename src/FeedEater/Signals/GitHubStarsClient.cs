using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Signals;

public sealed record Star(string FullName, string Url, string? Description, IReadOnlyList<string> Topics, DateTimeOffset StarredAt);

/// <summary>Public stars, unauthenticated (60 requests an hour is plenty for one run a day).</summary>
public sealed class GitHubStarsClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    /// <summary>Null when GitHub answers anything but success (404, a 403 rate limit): the caller just goes without facts.</summary>
    public async Task<RepoFacts?> RepoFactsAsync(string owner, string repo, CancellationToken ct)
    {
        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        using var response = await http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        string? tag = null;
        DateTimeOffset? released = null;
        using var releaseResponse = await http.GetAsync($"{path}/releases/latest", ct);
        if (releaseResponse.IsSuccessStatusCode)
        {
            var release = Json.Parse(await releaseResponse.Content.ReadAsStringAsync(ct));
            tag = Json.Str(release, "tag_name");
            released = release.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : null;
        }

        return new RepoFacts(
            Json.Str(body, "full_name") ?? $"{owner}/{repo}",
            body.GetProperty("created_at").GetDateTimeOffset(),
            body.TryGetProperty("pushed_at", out var pushed) && pushed.ValueKind == JsonValueKind.String ? pushed.GetDateTimeOffset() : null,
            body.TryGetProperty("stargazers_count", out var stars) ? stars.GetInt32() : 0,
            tag, released);
    }

    /// <summary>One read of the configured user; fails when GitHub does not know them.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync($"users/{Uri.EscapeDataString(options.Value.GitHub.User)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<Star>> PageAsync(int page, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"users/{options.Value.GitHub.User}/starred?sort=created&direction=desc&per_page=100&page={page}");
        request.Headers.Accept.ParseAdd("application/vnd.github.star+json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.EnumerateArray().Select(s =>
        {
            var repo = s.GetProperty("repo");
            return new Star(
                repo.GetProperty("full_name").GetString()!,
                repo.GetProperty("html_url").GetString()!,
                Json.Str(repo, "description"),
                repo.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
                s.GetProperty("starred_at").GetDateTimeOffset());
        }).ToList();
    }
}
