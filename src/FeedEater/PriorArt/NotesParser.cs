using System.Text.RegularExpressions;

namespace FeedEater.PriorArt;

/// <summary>One entry of a <c>personal/research</c> notes file (<c>### title</c> plus URL, source date, retrieved, fact, quote, label).</summary>
internal sealed record NoteEntry(int Line, string Title, string Url, DateOnly? SourceDate, DateOnly? Retrieved, string Fact, string? Quote, string? Label);

internal static partial class NotesParser
{
    [GeneratedRegex(@"^-\s+(URL|Source date|Retrieved|Fact|Quote|Label):\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Field();

    /// <summary>Entries with a URL and a fact. An entry without either is dropped: no source, no fact.</summary>
    public static IReadOnlyList<NoteEntry> Parse(string markdown)
    {
        var entries = new List<NoteEntry>();
        string? title = null;
        var start = 0;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (title is not null && fields.TryGetValue("URL", out var url) && fields.TryGetValue("Fact", out var fact) && url.Length > 0 && fact.Length > 0)
            {
                entries.Add(new NoteEntry(
                    start, title, url.Trim('<', '>', ' '), Date(fields, "Source date"), Date(fields, "Retrieved"), fact,
                    fields.TryGetValue("Quote", out var q) ? q.Trim().Trim('"', '“', '”') : null,
                    fields.TryGetValue("Label", out var l) ? l.Trim() : null));
            }

            fields.Clear();
        }

        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                (title, start) = (PriorArtText.Plain(line[4..]), i + 1);
            }
            else if (line.StartsWith('#'))
            {
                Flush();
                title = null;
            }
            else if (title is not null && Field().Match(line) is { Success: true } m)
            {
                fields[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
        }

        Flush();
        return entries;
    }

    private static DateOnly? Date(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var text) && PriorArtText.DateInName(text) is { } d ? d : null;
}
