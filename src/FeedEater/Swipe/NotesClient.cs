using System.Text.Json;

namespace FeedEater.Swipe;

public sealed record Note(string Id, string Topic, string? ConversationTitle, IReadOnlyList<string> Points);

/// <summary>Reads notes from a server that answers <c>GET /api/v1/notes</c> with <c>{ items: [{ id, topic, points, conversationTitle }], nextBefore }</c>, newest first.</summary>
public sealed class NotesClient(HttpClient http)
{
    private const int PageSize = 100;
    private const int MaxPages = 10;

    public async Task<IReadOnlyList<Note>> ListAsync(CancellationToken ct)
    {
        var notes = new List<Note>();
        string? before = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"api/v1/notes?limit={PageSize}{(before is null ? "" : "&before=" + Uri.EscapeDataString(before))}";
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var points = item.GetProperty("points").EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToList();
                if (item.GetProperty("id").GetString() is { Length: > 0 } id && item.GetProperty("topic").GetString() is { Length: > 0 } topic && points.Count > 0)
                {
                    notes.Add(new Note(id, topic, item.TryGetProperty("conversationTitle", out var t) ? t.GetString() : null, points));
                }
            }

            before = doc.RootElement.TryGetProperty("nextBefore", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (before is null)
            {
                break;
            }
        }

        return notes;
    }
}
