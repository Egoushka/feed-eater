using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FeedEater.Cards;
using FeedEater.Trials;

namespace FeedEater.Radar;

public sealed class RadarExportException(string message) : Exception(message);

/// <summary>Cards to radar.json: the states a radar shows, personal repos only, nothing a deny-listed term touches.</summary>
public static partial class RadarRules
{
    public static readonly string[] Rings = ["adopt", "trial", "assess", "hold"];
    public const int MaxName = 80;
    public const int MaxReason = 200;
    public const int MaxResult = 500;

    /// <summary>
    /// The radar.json text. A card of a repo that is not kind <c>personal</c> in <paramref name="manifest"/> (work, infra, unknown, the shared skills file) gets no row.
    /// Throws <see cref="RadarExportException"/> when there is no deny list or a row names one of its terms: nothing is partly exported.
    /// </summary>
    public static string Build(
        IReadOnlyList<CardRow> cards, IReadOnlyList<ManifestRepo> manifest, IReadOnlyList<InventoryItem> inventory,
        IReadOnlyDictionary<long, string> results, DenyList? deny)
    {
        if (deny is null)
        {
            throw new RadarExportException("Cards:DenyTermsFile is not set or not readable; the radar is not exported without it");
        }

        var personal = manifest.Where(r => r.Kind == "personal").Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new JsonArray();
        foreach (var card in cards
            .Where(c => Rings.Contains(c.State, StringComparer.Ordinal) && personal.Contains(c.Repo))
            .OrderBy(c => Array.IndexOf(Rings, c.State)).ThenByDescending(c => c.StateChangedAt ?? c.CreatedAt).ThenBy(c => c.Title, StringComparer.Ordinal))
        {
            var kind = inventory.FirstOrDefault(i => string.Equals(i.Repo, card.Repo, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Term, card.Term, StringComparison.OrdinalIgnoreCase))?.Kind;
            var entry = new JsonObject
            {
                ["name"] = Cut(card.Title, MaxName),
                ["quadrant"] = kind == "image" ? "infra" : "tooling",
                ["ring"] = card.State,
                ["since"] = DateOnly.FromDateTime(card.StateChangedAt ?? card.CreatedAt).ToString("yyyy-MM-dd"),
                ["reason"] = Cut(card.StateReason, MaxReason),
                ["repoKind"] = "personal",
            };
            if (results.TryGetValue(card.Id, out var result) && Cut(result, MaxResult) is { Length: > 0 } summary)
            {
                entry["result"] = summary;
            }

            var texts = new[] { entry["name"], entry["reason"], entry["result"] }.Select(n => (string?)n).ToArray();
            if (deny.Matches(texts))
            {
                throw new RadarExportException($"card {card.Id} names a term of the deny list; the radar is not exported");
            }

            entries.Add(entry);
        }

        // No "generated" stamp: the same cards give the same file, so an export with nothing new opens no pull request.
        var doc = new JsonObject { ["entries"] = entries };
        var text = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (RadarSchema.Check(text) is { Count: > 0 } problems)
        {
            throw new RadarExportException("radar.json does not match the site schema: " + string.Join("; ", problems.Take(5)));
        }

        return text;
    }

    /// <summary>The first line-collapsed <paramref name="max"/> characters, cut at a word where there is one.</summary>
    internal static string Cut(string text, int max)
    {
        var flat = Whitespace().Replace(text, " ").Trim();
        if (flat.Length <= max)
        {
            return flat;
        }

        var cut = flat[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// The site's content/radar.schema.json (embedded copy), read for the subset it uses: type, enum, const, required, properties,
/// additionalProperties, items, $ref into $defs, minLength, maxLength, pattern.
/// </summary>
public static class RadarSchema
{
    private static readonly Lazy<JsonElement> Schema = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FeedEater.Radar.schema.json")
            ?? throw new InvalidOperationException("the radar schema is not embedded");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    public static IReadOnlyList<string> Check(string json) => Check(JsonDocument.Parse(json).RootElement, Schema.Value);

    public static IReadOnlyList<string> Check(JsonElement value, JsonElement schema)
    {
        var problems = new List<string>();
        Walk(value, schema, schema, "$", problems);
        return problems;
    }

    private static void Walk(JsonElement value, JsonElement schema, JsonElement root, string at, List<string> problems)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = root;
            foreach (var part in reference.GetString()!.TrimStart('#', '/').Split('/'))
            {
                target = target.GetProperty(part);
            }

            Walk(value, target, root, at, problems);
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(value, constant))
        {
            problems.Add($"{at}: must be {constant}");
        }

        if (schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(value, o)))
        {
            problems.Add($"{at}: must be one of {string.Join(", ", options.EnumerateArray())}");
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var ok = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                _ => true,
            };
            if (!ok)
            {
                problems.Add($"{at}: must be {type}");
                return;
            }
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var min) && s.Length < min.GetInt32())
            {
                problems.Add($"{at}: shorter than {min}");
            }

            if (schema.TryGetProperty("maxLength", out var max) && s.Length > max.GetInt32())
            {
                problems.Add($"{at}: longer than {max}");
            }

            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(s, pattern.GetString()!))
            {
                problems.Add($"{at}: does not match {pattern}");
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in value.EnumerateArray())
            {
                Walk(item, items, root, $"{at}[{i++}]", problems);
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                problems.AddRange(required.EnumerateArray().Select(r => r.GetString()!).Where(r => !value.TryGetProperty(r, out _)).Select(r => $"{at}: missing {r}"));
            }

            schema.TryGetProperty("properties", out var props);
            var closed = schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False;
            foreach (var member in value.EnumerateObject())
            {
                if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(member.Name, out var memberSchema))
                {
                    Walk(member.Value, memberSchema, root, $"{at}.{member.Name}", problems);
                }
                else if (closed)
                {
                    problems.Add($"{at}: unknown field {member.Name}");
                }
            }
        }
    }
}
