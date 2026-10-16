using System.IO.Compression;

namespace FeedEater.Instagram;

public sealed record ExportEntry(string Path, long Size);

/// <summary>
/// The files of an Instagram "Download your information" export, read in place: a .zip is streamed entry by entry and a folder is walked. Nothing is
/// extracted or written, so a hostile path inside an archive cannot land anywhere.
/// </summary>
public sealed class ExportReader : IDisposable
{
    public const int MaxEntries = 50_000;

    private readonly ZipArchive? _zip;
    private readonly string? _root;

    private ExportReader(ZipArchive? zip, string? root, IReadOnlyList<ExportEntry> entries, bool truncated)
    {
        _zip = zip;
        _root = root;
        Entries = entries;
        Truncated = truncated;
    }

    public IReadOnlyList<ExportEntry> Entries { get; }

    /// <summary>The export holds more than <see cref="MaxEntries"/> files; the rest are not listed.</summary>
    public bool Truncated { get; }

    public bool IsZip => _zip is not null;

    public static ExportReader Open(string path)
    {
        if (Directory.Exists(path))
        {
            var root = Path.GetFullPath(path);
            var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Take(MaxEntries + 1).ToList();
            return new ExportReader(null, root,
                files.Take(MaxEntries).Select(f => new ExportEntry(Path.GetRelativePath(root, f).Replace('\\', '/'), new FileInfo(f).Length)).Order(new ByPath()).ToList(), files.Count > MaxEntries);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No such file or folder: {path}");
        }

        var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(e => e.Name.Length > 0).Take(MaxEntries + 1).ToList();
        return new ExportReader(zip, null, entries.Take(MaxEntries).Select(e => new ExportEntry(e.FullName, e.Length)).Order(new ByPath()).ToList(), entries.Count > MaxEntries);
    }

    public Stream Read(string entryPath)
    {
        if (_zip is not null)
        {
            return (_zip.GetEntry(entryPath) ?? throw new FileNotFoundException(entryPath)).Open();
        }

        var full = Path.GetFullPath(Path.Combine(_root!, entryPath));
        if (!full.StartsWith(_root! + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{entryPath} is outside the export folder");
        }

        return File.OpenRead(full);
    }

    public void Dispose() => _zip?.Dispose();

    private sealed class ByPath : IComparer<ExportEntry>
    {
        public int Compare(ExportEntry? a, ExportEntry? b) => string.CompareOrdinal(a?.Path, b?.Path);
    }
}
