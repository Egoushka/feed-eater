using System.Globalization;
using System.Text;

namespace FeedEater.PriorArt;

/// <summary>One repository of a GitHub snapshot CSV (<c>repo,stars,created,pushed,topics,license,has_paper</c>), as <c>gh api</c> reported it.</summary>
internal sealed record SnapshotRow(string Repo, long? Stars, DateOnly? Pushed, string? License);

internal static class SnapshotCsv
{
    public static IReadOnlyList<SnapshotRow> Parse(string csv)
    {
        var records = Records(csv);
        if (records.Count == 0)
        {
            return [];
        }

        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var (repo, stars, pushed, license) = (Col("repo"), Col("stars"), Col("pushed"), Col("license"));
        if (repo < 0)
        {
            return [];
        }

        string? At(List<string> r, int c) => c >= 0 && c < r.Count && r[c].Length > 0 ? r[c].Trim() : null;
        return records.Skip(1)
            .Select(r => (Repo: At(r, repo), Row: r))
            .Where(x => x.Repo is { Length: > 0 })
            .Select(x => new SnapshotRow(
                x.Repo!,
                long.TryParse(At(x.Row, stars), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null,
                DateOnly.TryParseExact(At(x.Row, pushed), "yyyy-MM-dd", out var p) ? p : null,
                At(x.Row, license)))
            .ToList();
    }

    /// <summary>RFC 4180: quoted fields may hold commas, quotes (doubled) and line breaks.</summary>
    private static List<List<string>> Records(string csv)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    records.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            records.Add(row);
        }

        return records.Where(r => r.Count > 1 || r.Count == 1 && r[0].Length > 0).ToList();
    }
}
