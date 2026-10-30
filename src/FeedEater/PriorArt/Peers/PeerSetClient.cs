using Microsoft.Extensions.Options;

namespace FeedEater.PriorArt.Peers;

/// <summary>The peer set file as text, read from the GitHub contents API; null when no URL is set.</summary>
public sealed class PeerSetClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    public async Task<string?> TextAsync(CancellationToken ct)
    {
        var o = options.Value.PeerHarvest;
        if (o.SetUrl.Length == 0)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, o.SetUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        request.Headers.UserAgent.ParseAdd("feed-eater");
        if (o.SetToken.Length > 0)
        {
            request.Headers.Authorization = new("Bearer", o.SetToken);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
