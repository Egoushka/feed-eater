using System.Text;

namespace FeedEater.Push;

/// <summary>Publishes one ntfy message. The token travels in a header only and is never logged; callers log the outcome, not the request.</summary>
public sealed class NtfyClient(HttpClient http)
{
    public async Task PostAsync(string topic, string title, string message, string? click, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Uri.EscapeDataString(topic))
        {
            Content = new StringContent(message, Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("Title", title);
        if (!string.IsNullOrEmpty(click))
        {
            request.Headers.Add("Click", click);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
