using System.Text.Json;

namespace FeedEater.PriorArt.Peers;

/// <summary>One package GitHub's dependency graph found, with its purl split at the version.</summary>
public sealed record SbomPackage(string Purl, string Name, string? Version);

/// <summary>Parses GitHub's SPDX <c>dependency-graph/sbom</c> response into one row per package with a purl external ref.</summary>
public static class SbomParser
{
    public static IReadOnlyList<SbomPackage> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("sbom", out var sbom) || !sbom.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SbomPackage>();
        foreach (var pkg in packages.EnumerateArray())
        {
            if (Purl(pkg) is not { } purl)
            {
                continue;   // the root/document package has no purl external ref
            }

            var (bare, versionFromPurl) = Split(purl);
            var versionInfo = Json.Str(pkg, "versionInfo");
            var version = versionInfo is null or "NOASSERTION" ? versionFromPurl : versionInfo;
            result.Add(new SbomPackage(bare, Json.Str(pkg, "name") ?? bare, version));
        }

        return result.DistinctBy(p => p.Purl, StringComparer.Ordinal).OrderBy(p => p.Purl, StringComparer.Ordinal).ToList();
    }

    private static string? Purl(JsonElement pkg)
    {
        if (!pkg.TryGetProperty("externalRefs", out var refs) || refs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var r in refs.EnumerateArray())
        {
            if (Json.Str(r, "referenceType") == "purl" && Json.Str(r, "referenceLocator") is { Length: > 0 } locator)
            {
                return locator;
            }
        }

        return null;
    }

    /// <summary>The purl without its trailing <c>@version</c> (the external ref the package entity uses), and that version text.</summary>
    private static (string Purl, string? Version) Split(string purl)
    {
        var at = purl.LastIndexOf('@');
        return at > 0 ? (purl[..at], purl[(at + 1)..]) : (purl, null);
    }
}
