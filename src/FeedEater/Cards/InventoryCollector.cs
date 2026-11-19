using Microsoft.Extensions.Options;

namespace FeedEater.Cards;

/// <summary>
/// Reads the owner's repos from a mounted checkout, read-only: the manifest names them, only kind personal and infra are opened,
/// symlinks are never followed and a path that leaves the root is skipped. Nothing read is logged beyond counts.
/// </summary>
public sealed class InventoryCollector(IOptions<FeedEaterOptions> options, CardStore store, ILogger<InventoryCollector> logger)
{
    private const int MaxDepth = 6;
    private const int MaxFiles = 400;
    private const long MaxBytes = 1_000_000;
    private static readonly HashSet<string> SkipDirs = new(StringComparer.Ordinal)
        { ".git", "node_modules", "bin", "obj", ".claude", ".venv", "vendor", "dist", ".devbox", ".next", "target", "scratch", "archive" };

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var o = options.Value.Cards;
        if (o.Root.Length == 0 || !Directory.Exists(o.Root))
        {
            logger.LogWarning("Cards:Root is not set or does not exist; no inventory collected");
            return 0;
        }

        var (found, read) = Scan(o, ct);
        await store.ReplaceInventoryAsync(found, read, ct);
        logger.LogInformation("Inventory: {Items} items from {Repos} repos", found.Count, read.Count);
        return found.Count;
    }

    /// <summary>The items found and the repos that were readable. Files only: no database, no network.</summary>
    internal (List<InventoryItem> Found, List<string> Read) Scan(CardsOptions o, CancellationToken ct)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(o.Root));
        var found = new List<InventoryItem>();
        var read = new List<string>();
        var manifest = ReadText(Path.Combine(root, o.Manifest));
        foreach (var repo in manifest is null ? [] : InventoryParsers.Manifest(manifest))
        {
            var dir = Path.GetFullPath(Path.Combine(root, repo.Path));
            if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(dir) || IsLink(dir))
            {
                continue;
            }

            read.Add(repo.Name);
            var budget = MaxFiles;
            foreach (var file in Files(dir, 0))
            {
                if (budget-- <= 0)
                {
                    logger.LogWarning("Inventory of {Repo} stopped at {Max} files", repo.Name, MaxFiles);
                    break;
                }

                ct.ThrowIfCancellationRequested();
                if (ReadText(file) is { } text)
                {
                    found.AddRange(Parse(Path.GetFileName(file), text, repo.Name, Path.GetRelativePath(dir, file).Replace('\\', '/')));
                }
            }
        }

        if (o.InventoryToml.Length > 0 && InventoryParsers.SafeRelative(o.InventoryToml) && ReadText(Path.Combine(root, o.InventoryToml)) is { } toml)
        {
            found.AddRange(InventoryParsers.InventoryToml(toml, "workspace", o.InventoryToml));
            read.Add("workspace");
        }

        return (found, read);
    }

    internal static IReadOnlyList<InventoryItem> Parse(string fileName, string text, string repo, string path) => fileName switch
    {
        _ when fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) => InventoryParsers.Csproj(text, repo, path),
        "package.json" => InventoryParsers.PackageJson(text, repo, path),
        _ => InventoryParsers.Compose(text, repo, path),
    };

    private static bool Wanted(string name) =>
        name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name is "Directory.Packages.props" or "package.json"
        || ((name.StartsWith("compose", StringComparison.OrdinalIgnoreCase) || name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase))
            && (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> Files(string dir, int depth)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (IsLink(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (depth < MaxDepth && !SkipDirs.Contains(Path.GetFileName(entry)))
                {
                    foreach (var f in Files(entry, depth + 1))
                    {
                        yield return f;
                    }
                }
            }
            else if (Wanted(Path.GetFileName(entry)))
            {
                yield return entry;
            }
        }
    }

    private static bool IsLink(string path) => new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && !IsLink(path) && info.Length <= MaxBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
