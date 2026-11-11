using FeedEater.PriorArt;
using FeedEater.Storage;
using FeedEater.Text;

namespace FeedEater.WeeklyIdeas;

public sealed record NotesImportCounts(int Files, int Imported, int AlreadyThere, int Skipped);

/// <summary>
/// Reads a folder of research notes (Markdown, <c>### title</c> entries with URL, source date and fact; see <see cref="NotesParser"/>)
/// into the archive as items, one per finding. A finding whose URL is already an item, from a feed or an earlier import, is left out.
/// They are archived and searchable but marked backfilled, so they are never embedded or sent in a digest; the weekly ideas run
/// reads them back as <see cref="Signal"/> leads.
/// </summary>
public sealed class ResearchNotesImporter(ItemStore items, TimeProvider time)
{
    public const string Signal = "research-note";

    /// <summary>The source key prefix of an imported note; the rest is the finding's canonical URL.</summary>
    public const string KeyPrefix = "research:";

    private const int MaxUrlLength = 2048;

    public async Task<NotesImportCounts> ImportAsync(string folder, CancellationToken ct)
    {
        int files = 0, imported = 0, already = 0, skipped = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            files++;
            foreach (var entry in NotesParser.Parse(await File.ReadAllTextAsync(path, ct)))
            {
                if (entry.Url.Length > MaxUrlLength || !Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    skipped++;
                    continue;
                }

                var canonical = UrlCanonicalizer.Canonical(entry.Url);
                if (await items.FindByCanonicalAsync(canonical, ct) is not null)
                {
                    already++;
                    continue;
                }

                var id = await items.InsertAsync(new NewItem(
                    null, null, entry.Url, canonical, TitleHash.Of(entry.Title), entry.Title, Published(entry), Content(entry),
                    SourceKey: KeyPrefix + canonical, Backfilled: true), ct);
                _ = id is null ? already++ : imported++;
            }
        }

        return new NotesImportCounts(files, imported, already, skipped);
    }

    /// <summary>The finding's own date, else the day it was retrieved, else now.</summary>
    private DateTime Published(NoteEntry e) =>
        (e.SourceDate ?? e.Retrieved) is { } d ? d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : time.GetUtcNow().UtcDateTime;

    internal static string Content(NoteEntry e) =>
        string.Join('\n', new[] { e.Fact, e.Quote is { Length: > 0 } q ? $"\"{q}\"" : null, e.Label }.Where(s => !string.IsNullOrEmpty(s)));
}
