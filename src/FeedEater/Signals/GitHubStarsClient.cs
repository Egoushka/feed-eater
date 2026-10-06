using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Signals;

public sealed record Star(string FullName, string Url, string? Description, IReadOnlyList<string> Topics, DateTimeOffset StarredAt);

/// <summary>Found: the facts are complete. Gone: 404. RateLimited: 403 or 429. Failed: any other status.</summary>
public enum LookupOutcome { Found, Gone, RateLimited, Failed }

/// <summary>What GitHub said about one repo and how many requests that took (the rate limit counts them). <see cref="Facts"/> is set for Found, and for RateLimited when only the release call was refused.</summary>
public sealed record RepoLookup(LookupOutcome Outcome, RepoFacts? Facts, int Requests);

/// <summary>Public repo data, with <c>GitHub:Token</c> when set (60 requests an hour without it, which is plenty for one run a day).</summary>
public sealed class GitHubStarsClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    /// <summary>Null when GitHub answers anything but success (404, a 403 rate limit): the caller just goes without facts.</summary>
    public async Task<RepoFacts?> RepoFactsAsync(string owner, string repo, CancellationToken ct) => (await LookupAsync(owner, repo, ct)).Facts;

    /// <summary>As <see cref="RepoFactsAsync"/>, but says why there are no facts, so a caller can tell a deleted repo from a refused call.</summary>
    public async Task<RepoLookup> LookupAsync(string owner, string repo, CancellationToken ct)
    {
        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        using var response = await http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new RepoLookup(Failure(response.StatusCode), null, 1);
        }

        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        string? tag = null;
        DateTimeOffset? released = null;
        var outcome = LookupOutcome.Found;
        using var releaseResponse = await http.GetAsync($"{path}/releases/latest", ct);
        if (releaseResponse.IsSuccessStatusCode)
        {
            var release = Json.Parse(await releaseResponse.Content.ReadAsStringAsync(ct));
            tag = Json.Str(release, "tag_name");
            released = release.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : null;
        }
        else if (Failure(releaseResponse.StatusCode) == LookupOutcome.RateLimited)
        {
            outcome = LookupOutcome.RateLimited;   // "no release" would read as a change, so the caller must not trust the tag
        }

        var facts = new RepoFacts(
            Json.Str(body, "full_name") ?? $"{owner}/{repo}",
            body.GetProperty("created_at").GetDateTimeOffset(),
            body.TryGetProperty("pushed_at", out var pushed) && pushed.ValueKind == JsonValueKind.String ? pushed.GetDateTimeOffset() : null,
            body.TryGetProperty("stargazers_count", out var stars) ? stars.GetInt32() : 0,
            tag, released,
            body.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True);
        return new RepoLookup(outcome, facts, 2);
    }

    private static LookupOutcome Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => LookupOutcome.Gone,
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => LookupOutcome.RateLimited,
        _ => LookupOutcome.Failed,
    };

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
