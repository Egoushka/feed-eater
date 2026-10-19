using Microsoft.Extensions.Options;

namespace FeedEater.WeeklyIdeas;

/// <summary>The backlog file as text, read from the GitHub contents API; null when no URL is set.</summary>
public sealed class BacklogClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    public async Task<string?> TextAsync(CancellationToken ct)
    {
        var o = options.Value.WeeklyIdeas;
        if (o.BacklogUrl.Length == 0)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, o.BacklogUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        request.Headers.UserAgent.ParseAdd("feed-eater");
        if (o.BacklogToken.Length > 0)
        {
            request.Headers.Authorization = new("Bearer", o.BacklogToken);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
