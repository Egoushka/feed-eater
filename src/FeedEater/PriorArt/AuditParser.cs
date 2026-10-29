using System.Text.RegularExpressions;

namespace FeedEater.PriorArt;

/// <summary>
/// One table row of an audit, reduced to what the base stores. <see cref="Verdict"/> is adopt, pilot, watch or skip. A
/// <see cref="LabelRow"/> comes from a label | value table about the default subject: its evidence is a pointer, not a fact.
/// </summary>
internal sealed record AuditRow(
    int Line, string Section, string Index, string Name, string? Url, string? Description, string? Evidence, string? Verdict, string? Reason, bool LabelRow = false);

/// <summary>
/// Reads the pipe tables of an audit markdown file. A table is used when it has a subject column (candidate, tool, repo, item,
/// project, gap, name) and at least one of a description (what, why now), an evidence or a verdict column; a table with no subject
/// column but a default subject is read as label | value rows. Other tables are skipped.
/// </summary>
internal static partial class AuditParser
{
    private static readonly string[] Verdicts = ["adopt", "pilot", "watch", "skip"];

    private static readonly string[] NameEnds = [", ", " (", ": ", " — ", " – ", " - "];

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(candidate|tool|repo|item|project|gap|name)\b")]
    private static partial Regex SubjectHeader();

    public static IReadOnlyList<AuditRow> Parse(string markdown, string? defaultSubjectName)
    {
        var lines = markdown.Split('\n');
        var rows = new List<AuditRow>();
        var section = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith('#'))
            {
                section = PriorArtText.Plain(line.TrimStart('#'));
                continue;
            }

            if (!line.TrimStart().StartsWith('|') || i + 1 >= lines.Length || !Separator().IsMatch(lines[i + 1]))
            {
                continue;
            }

            var headers = Cells(line).Select(h => PriorArtText.Plain(h).ToLowerInvariant()).ToList();
            var end = i + 2;
            while (end < lines.Length && lines[end].TrimStart().StartsWith('|'))
            {
                end++;
            }

            var columns = Columns(headers);
            for (var r = i + 2; r < end; r++)
            {
                var cells = Cells(lines[r]);
                var row = columns is null ? null : ReadRow(columns, cells, r + 1, section, defaultSubjectName);
                if (row is not null)
                {
                    rows.Add(row);
                }
            }

            i = end - 1;
        }

        return rows;
    }

    /// <summary>The verdict word and the reason of a verdict cell, or null when it does not start with one of the four words.</summary>
    public static (string Verdict, string Reason)? VerdictOf(string cell)
    {
        var plain = PriorArtText.Plain(cell);
        var word = new string(plain.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        return Verdicts.Contains(word) ? (word, plain[word.Length..].TrimStart(' ', '.', ',', ':', '—', '-').Trim()) : null;
    }

    private sealed record Map(int Index, int Subject, int What, int Evidence, int Verdict, bool Dropped, int Label, int Value);

    private static Map? Columns(List<string> headers)
    {
        int Find(Func<string, bool> match) => headers.FindIndex(h => match(h));
        var index = Find(h => h == "#");
        var subject = Find(h => SubjectHeader().IsMatch(h));
        var what = Find(h => h.StartsWith("what it", StringComparison.Ordinal) || h.StartsWith("why now", StringComparison.Ordinal));
        if (what < 0 && subject >= 0)
        {
            what = Find(h => h == "what" || h == "fact");
        }

        var evidence = Find(h => h.StartsWith("evidence", StringComparison.Ordinal));
        var verdict = Find(h => h.StartsWith("verdict", StringComparison.Ordinal));
        var dropped = verdict < 0 ? Find(h => h.Contains("dropped", StringComparison.Ordinal)) : -1;
        if (subject >= 0)
        {
            return what >= 0 || evidence >= 0 || verdict >= 0 || dropped >= 0 ? new Map(index, subject, what, evidence, verdict, dropped >= 0, -1, -1) : null;
        }

        var text = Enumerable.Range(0, headers.Count).Where(c => c != index && c != evidence).Take(2).ToList();
        return text.Count == 2 ? new Map(index, -1, -1, evidence, -1, false, text[0], text[1]) : null;
    }

    private static AuditRow? ReadRow(Map map, List<string> cells, int line, string section, string? defaultName)
    {
        string Cell(int c) => c >= 0 && c < cells.Count ? cells[c] : "";
        var evidence = Cell(map.Evidence) is { Length: > 0 } e ? PriorArtText.Plain(e) : null;
        var index = PriorArtText.Plain(Cell(map.Index));
        if (map.Subject < 0)
        {
            var (label, value) = (PriorArtText.Plain(Cell(map.Label)), PriorArtText.Plain(Cell(map.Value)));
            return defaultName is null || label.Length == 0 || value.Length == 0
                ? null
                : new AuditRow(line, section, index, defaultName, null, $"{label}: {value}", evidence, null, null, LabelRow: true);
        }

        var subject = Cell(map.Subject);
        var plain = PriorArtText.Plain(subject);
        if (plain.Length == 0)
        {
            return null;
        }

        var (name, url) = NameOf(subject, plain);
        var description = Cell(map.What) is { Length: > 0 } w
            ? PriorArtText.Plain(w)
            : plain.Length > name.Length + 20 ? plain : null;
        string? verdict = null, reason = null;
        if (map.Dropped)
        {
            (verdict, reason) = ("skip", PriorArtText.Plain(Cell(map.Verdict >= 0 ? map.Verdict : map.Subject + 1)));
        }
        else if (Cell(map.Verdict) is { Length: > 0 } v && VerdictOf(v) is var (word, why))
        {
            (verdict, reason) = (word, why);
        }

        return description is null && evidence is null && verdict is null ? null : new AuditRow(line, section, index, name, url, description, evidence, verdict, reason);
    }

    private static (string Name, string? Url) NameOf(string markdown, string plain)
    {
        var link = PriorArtText.FirstLink(markdown);
        if (link is { } l && plain.StartsWith(l.Text, StringComparison.OrdinalIgnoreCase) && l.Text.Length > 0)
        {
            return (PriorArtText.Cut(l.Text, 60), l.Url);
        }

        var end = NameEnds.Select(s => plain.IndexOf(s, StringComparison.Ordinal)).Where(p => p >= 2).DefaultIfEmpty(plain.Length).Min();
        var name = PriorArtText.Cut(plain[..end], 60);
        var accepted = link is { } k && (PriorArtText.RefFromUrl(k.Url) is { } r && !r.StartsWith("url:", StringComparison.Ordinal) || k.Text.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        return (name, accepted ? link!.Value.Url : null);
    }

    /// <summary>Splits a table line at its unescaped pipes; <c>\|</c> stays a pipe in the cell.</summary>
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                current.Append('|');
                i++;
            }
            else if (text[i] == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(text[i]);
            }
        }

        if (current.ToString().Trim().Length > 0)
        {
            cells.Add(current.ToString().Trim());
        }

        return cells;
    }
}
