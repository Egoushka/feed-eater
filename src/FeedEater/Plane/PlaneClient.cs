using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Plane;

public sealed class PlaneClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    private static readonly string[] OpenGroups = ["backlog", "unstarted", "started"];
    private Dictionary<string, string>? _projectIds;

    private string Workspace => $"api/v1/workspaces/{options.Value.Plane.Workspace}";

    public async Task<string> ProjectIdAsync(string identifier, CancellationToken ct)
    {
        _projectIds ??= (await GetAsync($"{Workspace}/projects/", ct)).GetProperty("results").EnumerateArray()
            .ToDictionary(p => p.GetProperty("identifier").GetString()!, p => p.GetProperty("id").GetString()!, StringComparer.Ordinal);
        return _projectIds.TryGetValue(identifier, out var id) ? id : throw new InvalidOperationException($"Plane project {identifier} does not exist");
    }

    /// <summary>One read of the workspace's projects; fails on a bad key, URL or workspace slug.</summary>
    public async Task<int> ProjectCountAsync(CancellationToken ct) =>
        (await GetAsync($"{Workspace}/projects/", ct)).GetProperty("results").GetArrayLength();

    public async Task<IReadOnlyList<string>> OpenItemTitlesAsync(string identifier, CancellationToken ct)
    {
        var project = await ProjectIdAsync(identifier, ct);
        var open = (await GetAsync($"{Workspace}/projects/{project}/states/", ct)).GetProperty("results").EnumerateArray()
            .Where(s => OpenGroups.Contains(s.GetProperty("group").GetString()))
            .Select(s => s.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);

        JsonElement page;
        try
        {
            page = await GetAsync($"{Workspace}/projects/{project}/work-items/?per_page=100", ct);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Plane renamed issues to work-items; v1.4 may only have the old path (as plane-sync.py notes).
            page = await GetAsync($"{Workspace}/projects/{project}/issues/?per_page=100", ct);
        }

        return page.GetProperty("results").EnumerateArray()
            .Where(i => open.Contains(i.GetProperty("state").GetString()))
            .Select(i => i.GetProperty("name").GetString() ?? "")
            .Where(name => name.Length > 0)
            .ToList();
    }

    public async Task<string> CreateIntakeAsync(string identifier, string name, string descriptionHtml, CancellationToken ct)
    {
        var project = await ProjectIdAsync(identifier, ct);
        using var response = await http.PostAsJsonAsync(
            $"{Workspace}/projects/{project}/intake-issues/",
            new { issue = new { name, description_html = descriptionHtml, priority = "none" } }, ct);
        var issue = (await ReadAsync(response, ct)).GetProperty("issue");
        return issue.ValueKind == JsonValueKind.Object ? issue.GetProperty("id").GetString()! : issue.GetString()!;
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        return await ReadAsync(response, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Plane {(int)response.StatusCode}: {text[..Math.Min(300, text.Length)]}", null, response.StatusCode);
        }

        return Json.Parse(text);
    }
}
