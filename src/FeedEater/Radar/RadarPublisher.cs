using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Issues;

namespace FeedEater.Radar;

public sealed record RadarPublished(string Status, string? PrUrl);

/// <summary>Puts radar.json on a branch of the site repo and opens its pull request, or reuses the open one. Never merges.</summary>
public sealed class RadarPublisher(HttpClient http, IOptions<FeedEaterOptions> options)
{
    public async Task<RadarPublished> PublishAsync(string content, CancellationToken ct)
    {
        var o = options.Value.Radar;
        if (!GitHubIssues.IsRepo(o.Repo))
        {
            throw new RadarExportException($"Radar:Repo '{o.Repo}' is not a GitHub repo (owner/name)");
        }

        var repo = $"repos/{o.Repo}";
        var baseBranch = (await GetAsync(repo, ct))!.Value.GetProperty("default_branch").GetString()!;
        var onBase = await FileAsync(repo, o.Path, baseBranch, ct);
        if (onBase?.Text == content)
        {
            return new RadarPublished("unchanged", null);
        }

        var branchRef = $"{repo}/git/ref/heads/{Uri.EscapeDataString(o.Branch).Replace("%2F", "/")}";
        if (await GetAsync(branchRef, ct) is null)
        {
            var sha = (await GetAsync($"{repo}/git/ref/heads/{baseBranch}", ct))!.Value.GetProperty("object").GetProperty("sha").GetString();
            await SendAsync(HttpMethod.Post, $"{repo}/git/refs", new { @ref = $"refs/heads/{o.Branch}", sha }, ct);
        }

        var onBranch = await FileAsync(repo, o.Path, o.Branch, ct);
        if (onBranch?.Text != content)
        {
            await SendAsync(HttpMethod.Put, $"{repo}/contents/{o.Path}",
                new { message = "chore(radar): update radar.json from feed-eater", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), branch = o.Branch, sha = onBranch?.Sha }, ct);
        }

        var owner = o.Repo.Split('/')[0];
        var open = (await GetAsync($"{repo}/pulls?state=open&head={owner}:{Uri.EscapeDataString(o.Branch)}", ct))!.Value;
        if (open.GetArrayLength() > 0)
        {
            return new RadarPublished("updated", open[0].GetProperty("html_url").GetString());
        }

        var pr = await SendAsync(HttpMethod.Post, $"{repo}/pulls",
            new { title = "chore(radar): update radar.json", head = o.Branch, @base = baseBranch, body = "Exported by feed-eater from the adoption cards of personal repos. The site validates the file against content/radar.schema.json." }, ct);
        return new RadarPublished("opened", pr.GetProperty("html_url").GetString());
    }

    private sealed record Existing(string Sha, string Text);

    private async Task<Existing?> FileAsync(string repo, string path, string branch, CancellationToken ct)
    {
        if (await GetAsync($"{repo}/contents/{path}?ref={Uri.EscapeDataString(branch)}", ct) is not { } file)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!.Replace("\n", "")));
        return new Existing(file.GetProperty("sha").GetString()!, text);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        var owner = options.Value.Radar.Repo.Split('/')[0];
        if (options.Value.GitHub.TokenFor(owner) is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>The JSON body, or null for a 404.</summary>
    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync(response, ct);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = Request(method, path);
        request.Content = JsonContent.Create(body);
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

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
