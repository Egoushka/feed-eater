using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Signals;

public sealed record Star(string FullName, string Url, string? Description, IReadOnlyList<string> Topics, DateTimeOffset StarredAt);

/// <summary>Public stars, unauthenticated (60 requests an hour is plenty for one run a day).</summary>
public sealed class GitHubStarsClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
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
