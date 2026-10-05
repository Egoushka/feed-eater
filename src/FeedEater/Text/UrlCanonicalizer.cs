namespace FeedEater.Text;

/// <summary>One form per article: https, no www, no fragment, no tracking parameters, sorted query, no trailing slash.</summary>
public static class UrlCanonicalizer
{
    public static string Canonical(string url)
    {
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return trimmed;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : "";
        var query = string.Join('&', uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsTracking(p.Split('=')[0]))
            .Order(StringComparer.Ordinal));
        return $"https://{host}{port}{path}{(query.Length > 0 ? "?" + query : "")}";
    }

    private static bool IsTracking(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || key is "ref" or "fbclid" or "gclid" or "mc_cid" or "mc_eid";
}
