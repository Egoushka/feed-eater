using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FeedEater.Sources;

namespace FeedEater.Cards;

/// <summary>Something one of the owner's repos runs. <c>Kind</c> is nuget, npm, image, mcp or skill.</summary>
public sealed record InventoryItem(string Kind, string Name, string Version, string Repo, string SourcePath)
{
    /// <summary>The word looked for in feed titles: an image's last path segment, a package's id, a server's or skill's name.</summary>
    public string Term => Kind == "image" ? Name[(Name.LastIndexOf('/') + 1)..] : Name;
}

public sealed record ManifestRepo(string Name, string Path, string Kind, string Status);

/// <summary>Pure parsers: text in, inventory out. No file or network access, so every one is testable with a sample.</summary>
public static partial class InventoryParsers
{
    /// <summary>The repo kinds the board reads. A work repo is never read.</summary>
    public static readonly string[] ReadableKinds = ["personal", "infra"];

    public static IReadOnlyList<InventoryItem> Csproj(string xml, string repo, string path)
    {
        XElement? root;
        try
        {
            root = SafeXml.Root(xml);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        if (root is null)
        {
            return [];
        }

        var found = new List<InventoryItem>();
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion"))
        {
            var name = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var version = e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value ?? "";
            found.Add(new InventoryItem("nuget", name.Trim(), version.Trim(), repo, path));
        }

        return found;
    }

    public static IReadOnlyList<InventoryItem> PackageJson(string json, string repo, string path)
    {
        var found = new List<InventoryItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    found.AddRange(deps.EnumerateObject().Select(d => new InventoryItem("npm", d.Name, d.Value.ValueKind == JsonValueKind.String ? d.Value.GetString()! : "", repo, path)));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return found;
    }

    /// <summary>Every <c>image:</c> line of a Compose file. Line-based, so an anchor or a template does not break it.</summary>
    public static IReadOnlyList<InventoryItem> Compose(string yaml, string repo, string path)
    {
        var found = new List<InventoryItem>();
        foreach (Match m in ImageLine().Matches(yaml))
        {
            var reference = m.Groups[1].Value.Trim('"', '\'');
            if (reference.Contains('$', StringComparison.Ordinal) && !reference.Contains(':', StringComparison.Ordinal))
            {
                continue;   // ${IMAGE}: nothing to name
            }

            var digest = reference.IndexOf('@', StringComparison.Ordinal);
            if (digest >= 0)
            {
                reference = reference[..digest];
            }

            var slash = reference.LastIndexOf('/');
            var colon = reference.IndexOf(':', slash + 1);
            var name = colon < 0 ? reference : reference[..colon];
            var tag = colon < 0 ? "" : reference[(colon + 1)..];
            var firstSlash = name.IndexOf('/', StringComparison.Ordinal);
            if (firstSlash > 0 && (name[..firstSlash].Contains('.', StringComparison.Ordinal) || name[..firstSlash].Contains(':', StringComparison.Ordinal)))
            {
                name = name[(firstSlash + 1)..];   // drop the registry host
            }

            if (name.Length > 0)
            {
                found.Add(new InventoryItem("image", name, tag, repo, path));
            }
        }

        return found;
    }

    /// <summary>The <c>[[mcp]]</c> and <c>[[skill]]</c> tables of an inventory.toml: their <c>name</c> and, when present, <c>version</c>.</summary>
    public static IReadOnlyList<InventoryItem> InventoryToml(string toml, string repo, string path) =>
        Tables(toml).Where(t => t.Table is "mcp" or "skill" && t.Values.ContainsKey("name"))
            .Select(t => new InventoryItem(t.Table, t.Values["name"], t.Values.GetValueOrDefault("version", ""), repo, path)).ToList();

    /// <summary>The <c>[[repo]]</c> rows of repos.toml that are readable: kind personal or infra, not archived, a relative path with no <c>..</c>.</summary>
    public static IReadOnlyList<ManifestRepo> Manifest(string toml) =>
        Tables(toml).Where(t => t.Table == "repo" && t.Values.ContainsKey("name") && t.Values.ContainsKey("path"))
            .Select(t => new ManifestRepo(t.Values["name"], t.Values["path"], t.Values.GetValueOrDefault("kind", ""), t.Values.GetValueOrDefault("status", "")))
            .Where(r => ReadableKinds.Contains(r.Kind, StringComparer.Ordinal) && r.Status != "archived" && SafeRelative(r.Path))
            .ToList();

    internal static bool SafeRelative(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains('\\', StringComparison.Ordinal) && path.Split('/').All(s => s is not ".." and not "");

    /// <summary>A small TOML reader: <c>[[name]]</c> array tables and <c>key = "string"</c> lines. Everything else is skipped.</summary>
    private static List<(string Table, Dictionary<string, string> Values)> Tables(string toml)
    {
        var tables = new List<(string, Dictionary<string, string>)>();
        Dictionary<string, string>? current = null;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("[[", StringComparison.Ordinal) && line.Contains("]]", StringComparison.Ordinal))
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                tables.Add((line[2..line.IndexOf("]]", StringComparison.Ordinal)].Trim(), current));
            }
            else if (line.StartsWith('['))
            {
                current = null;
            }
            else if (current is not null && KeyValue().Match(line) is { Success: true } m)
            {
                current[m.Groups[1].Value] = m.Groups[2].Value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            }
        }

        return tables;
    }

    [GeneratedRegex(@"^\s*image:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex ImageLine();

    [GeneratedRegex("^([A-Za-z0-9_-]+)\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex KeyValue();
}
