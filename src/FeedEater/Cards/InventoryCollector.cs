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
        var root = RealPath(Path.GetFullPath(o.Root));
        var deny = DenyList.Load(o.DenyTermsFile);
        var found = new List<InventoryItem>();
        var read = new List<string>();
        var manifest = ReadText(root, Path.Combine(root, o.Manifest));
        foreach (var repo in manifest is null ? [] : InventoryParsers.Manifest(manifest))
        {
            var dir = Inside(root, Path.Combine(root, repo.Path));
            if (dir is null || !Directory.Exists(dir))
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
                if (ReadText(root, file) is { } text)
                {
                    found.AddRange(Parse(Path.GetFileName(file), text, repo.Name, Path.GetRelativePath(dir, file).Replace('\\', '/')));
                }
            }
        }

        // The skills and servers file names tools the owner uses everywhere, work ones too: it is read only with a deny list to filter it by.
        if (o.InventoryToml.Length > 0 && InventoryParsers.SafeRelative(o.InventoryToml))
        {
            if (deny is null)
            {
                logger.LogWarning("Cards:DenyTermsFile is not set or not readable; the skills and servers file is skipped");
            }
            else if (Inside(root, Path.Combine(root, o.InventoryToml)) is { } tomlPath && ReadText(root, tomlPath) is { } toml)
            {
                found.AddRange(InventoryParsers.InventoryToml(toml, "workspace", o.InventoryToml, deny));
                read.Add("workspace");
            }
        }

        if (deny is not null)
        {
            var before = found.Count;
            found.RemoveAll(i => deny.Matches(i.Name, i.SourcePath, i.Repo));
            if (found.Count < before)
            {
                logger.LogInformation("Inventory: {Dropped} items matched the deny list and were dropped", before - found.Count);
            }
        }

        return (found, read);
    }

    /// <summary>
    /// The canonical path of <paramref name="path"/> when it stays under <paramref name="root"/> after every symlink in it,
    /// ancestors included, is followed; otherwise null. <paramref name="root"/> must already be canonical.
    /// </summary>
    internal static string? Inside(string root, string path)
    {
        var real = RealPath(path);
        return real.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? real : null;
    }

    /// <summary>Resolves each path segment in turn, so a link in the middle of a path is followed, not only one at the end.</summary>
    internal static string RealPath(string path)
    {
        var current = Path.GetPathRoot(path)!;
        var pending = new Stack<string>(path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Reverse());
        for (var hops = 0; pending.Count > 0;)
        {
            var segment = pending.Pop();
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var next = Path.Combine(current, segment);
            var target = Directory.Exists(next) ? new DirectoryInfo(next).LinkTarget : File.Exists(next) ? new FileInfo(next).LinkTarget : null;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > 40)
            {
                throw new IOException("too many symbolic links");
            }

            var resolved = Path.GetFullPath(target, current);
            current = Path.GetPathRoot(resolved)!;
            foreach (var part in resolved[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                pending.Push(part);
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
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

    private static string? ReadText(string root, string path)
    {
        try
        {
            if (Inside(root, path) is not { } real)
            {
                return null;
            }

            var info = new FileInfo(real);
            return info.Exists && !IsLink(real) && info.Length <= MaxBytes ? File.ReadAllText(real) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
