using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Watch;

/// <summary>A watched upstream repo: the services that run it and the oldest running version among them (null when unknown).</summary>
public sealed record WatchedProduct(string Repo, IReadOnlyList<string> Services, string? RunningText, int[]? Running);

/// <summary>
/// What the owner runs. Primary: PINS.md from a file path or an HTTPS URL (the GitHub contents API works with an optional token).
/// Fallback when that is unset or fails: the static watch.json shipped with the app. The image-to-upstream map is watch-map.json.
/// Read on every call, so a change needs no restart.
/// </summary>
public sealed class WatchSource(HttpClient http, IOptions<FeedEaterOptions> options, ILogger<WatchSource> logger)
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "config");

    public async Task<IReadOnlyList<WatchedProduct>> LoadAsync(CancellationToken ct)
    {
        var o = options.Value.Watch;
        var pins = await FromSourceAsync(o, ct) ?? FromFallback(o);
        var map = LoadMap(o);
        return Group(pins, map);
    }

    internal static IReadOnlyList<WatchedProduct> Group(IReadOnlyList<Pinned> pins, IReadOnlyDictionary<string, string> map) => pins
        .Where(p => map.ContainsKey(p.Image))
        .GroupBy(p => map[p.Image].ToLowerInvariant())
        .Select(g =>
        {
            var versioned = g.Where(p => p.Version is not null).OrderBy(p => Versions.Parse(p.Version)!, Comparer<int[]>.Create(Versions.Compare)).ToList();
            var lowest = versioned.FirstOrDefault();
            return new WatchedProduct(g.Key, g.Select(p => p.Service).Distinct().Order().ToList(), lowest?.Version, lowest is null ? null : Versions.Parse(lowest.Version));
        })
        .OrderBy(p => p.Repo, StringComparer.Ordinal)
        .ToList();

    private async Task<IReadOnlyList<Pinned>?> FromSourceAsync(WatchOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Source))
        {
            return null;
        }

        try
        {
            string text;
            if (o.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, o.Source);
                request.Headers.UserAgent.ParseAdd("feed-eater");
                request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                request.Headers.Accept.ParseAdd("text/plain");
                if (o.Token.Length > 0)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.Token);
                }

                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                text = await response.Content.ReadAsStringAsync(ct);
            }
            else
            {
                text = await File.ReadAllTextAsync(o.Source, ct);
            }

            var pins = PinsParser.Parse(text);
            return pins.Count > 0 ? pins : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Only the host: the URL can carry a path or query a token has no business in a log.
            logger.LogWarning("PINS source {Host} unavailable ({Error}); using the static watch list", HostOf(o.Source), ex.GetType().Name);
            return null;
        }
    }

    internal static string HostOf(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.Host : "(file)";

    private IReadOnlyList<Pinned> FromFallback(WatchOptions o)
    {
        var path = o.FallbackPath.Length > 0 ? o.FallbackPath : Path.Combine(Dir, "watch.json");
        try
        {
            var rows = JsonSerializer.Deserialize<List<Pinned>>(File.ReadAllText(path), Json.Options);
            return rows ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Static watch list {Path} unreadable; watching nothing", path);
            return [];
        }
    }

    private IReadOnlyDictionary<string, string> LoadMap(WatchOptions o)
    {
        var path = o.MapPath.Length > 0 ? o.MapPath : Path.Combine(Dir, "watch-map.json");
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json.Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Watch map {Path} unreadable; watching nothing", path);
            return new Dictionary<string, string>();
        }
    }
}
