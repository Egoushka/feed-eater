using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PriorArt.Bench;

/// <summary>A minimal client for feed-eater's stateless <c>/mcp</c> endpoint: one tools/call per request.</summary>
internal sealed class Mcp(string baseUrl, string token) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        var data = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?[5..] ?? body;
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"{tool}: {error.GetRawText()}");
        }

        var result = doc.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            throw new InvalidOperationException($"{tool}: {result.GetProperty("content")[0].GetProperty("text").GetString()}");
        }

        return JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }

    public void Dispose() => http.Dispose();
}
