using System.Globalization;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FeedEater.Memory;

/// <summary>Hindsight's retain API.</summary>
public sealed class HindsightClient(HttpClient http)
{
    // Plain JSON to a trusted API, never HTML: the default encoder escapes "+" as \u002B, so "+03:00" would be sent as an escape sequence.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lists the banks: proves the URL and key work without needing the weekly bank to exist yet.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("v1/default/banks", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RetainAsync(
        string bank, string content, string context, DateTimeOffset timestamp, string documentId, IReadOnlyList<string> tags, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"v1/default/banks/{bank}/memories", new
        {
            @async = true,
            items = new[]
            {
                new
                {
                    content,
                    context,
                    timestamp = timestamp.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                    document_id = documentId,
                    tags,
                },
            },
        }, Options, ct);
        response.EnsureSuccessStatusCode();
    }
}
