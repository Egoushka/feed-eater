using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FeedEater.Issues;

/// <summary>GitHub issues of the repos ideas go to, with <c>GitHub:Token</c>. A repo is named <c>owner/name</c>.</summary>
public sealed partial class GitHubIssues(HttpClient http, IOptions<FeedEaterOptions>? options = null)
{
    /// <summary>The label every filed idea carries: the repo's triage queue.</summary>
    public const string TriageLabel = "triage";

    public static bool IsRepo(string? name) => name is not null && RepoName().IsMatch(name);

    /// <summary>The project <paramref name="named"/> means, ignoring case; a repo (owner/name) also answers to its bare name.</summary>
    public static string? Find(IEnumerable<string> projects, string? named) => named is null ? null
        : projects.FirstOrDefault(p => string.Equals(p, named, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => p.IndexOf('/') is var slash and > 0 && string.Equals(p[(slash + 1)..], named, StringComparison.OrdinalIgnoreCase));

    /// <summary>One read of the repo; fails when it is missing or the token cannot see it, and when the token could not label an issue there.</summary>
    public async Task CheckAsync(string repo, CancellationToken ct)
    {
        var body = await GetAsync(Path(repo), repo, ct);
        if (body.TryGetProperty("permissions", out var p) && p.TryGetProperty("push", out var push) && !push.GetBoolean())
        {
            throw new InvalidOperationException($"the token cannot write to {repo}: GitHub would drop the {TriageLabel} label");
        }
    }

    /// <summary>Titles of the repo's open issues, latest 100 first; pull requests left out.</summary>
    public Task<IReadOnlyList<string>> OpenTitlesAsync(string repo, CancellationToken ct) => TitlesAsync(repo, "open", ct);

    /// <summary>Titles of the repo's latest 100 issues, closed ones included: what an idea must not repeat.</summary>
    public Task<IReadOnlyList<string>> RecentTitlesAsync(string repo, CancellationToken ct) => TitlesAsync(repo, "all", ct);

    /// <summary>Opens an issue labelled <see cref="TriageLabel"/> and returns its number.</summary>
    public async Task<int> CreateAsync(string repo, string title, string body, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"{Path(repo)}/issues", repo);
        request.Content = JsonContent.Create(new { title, body, labels = new[] { TriageLabel } });
        using var response = await http.SendAsync(request, ct);
        return (await ReadAsync(response, ct)).GetProperty("number").GetInt32();
    }

    private async Task<IReadOnlyList<string>> TitlesAsync(string repo, string state, CancellationToken ct) =>
        (await GetAsync($"{Path(repo)}/issues?state={state}&sort=created&direction=desc&per_page=100", repo, ct)).EnumerateArray()
            .Where(i => !i.TryGetProperty("pull_request", out _))
            .Select(i => Json.Str(i, "title") ?? "")
            .Where(title => title.Length > 0)
            .ToList();

    private static string Path(string repo) => IsRepo(repo)
        ? $"repos/{repo}"
        : throw new InvalidOperationException($"'{repo}' is not a GitHub repo (owner/name)");

    /// <summary>A call to <paramref name="repo"/>'s API, with the token of its owner when <c>GitHub:OwnerTokens</c> has one.</summary>
    private HttpRequestMessage Request(HttpMethod method, string path, string repo)
    {
        var request = new HttpRequestMessage(method, path);
        if (options?.Value.GitHub.TokenFor(repo.Split('/')[0]) is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private async Task<JsonElement> GetAsync(string path, string repo, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path, repo);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync(response, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub {(int)response.StatusCode}: {text[..Math.Min(300, text.Length)]}", null, response.StatusCode);
        }

        return Json.Parse(text);
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepoName();
}
