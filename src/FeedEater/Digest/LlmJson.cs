using System.Globalization;
using System.Text.Json;

namespace FeedEater.Digest;

public sealed record TriageResult
{
    public int Relevance { get; init; }
    public string? Project { get; init; }
    public string Kind { get; init; } = "fyi";
    public string Reason { get; init; } = "";
}

public sealed record ReadResult
{
    public string Summary { get; init; } = "";
    public string Why { get; init; } = "";
    public string Kind { get; init; } = "fyi";
    public string? Project { get; init; }
    public string? Suggestion { get; init; }
}

public sealed record ReleaseNote(string Changes, string Breaking, string? Evidence);

/// <summary>What a reply to an item asks for. <see cref="UnknownProject"/> is a project he named that is not in the list.</summary>
public sealed record ReplyIntent(string Action, string? Project = null, string? UnknownProject = null, string? Idea = null, string? Question = null);

/// <summary>Lenient parsing of model replies: the first {...} in the text, unknown keys and kinds dropped.</summary>
public static class LlmJson
{
    private static readonly string[] Kinds = ["improve", "new", "fyi"];
    private static readonly string[] Actions = ["up", "down", "idea", "save", "mute", "ask", "unclear"];

    public static TriageResult Triage(string text, IReadOnlySet<string> keys)
    {
        if (ExtractObject(text) is not { } e)
        {
            return new TriageResult { Relevance = 1, Reason = "unparseable model output" };
        }

        return new TriageResult
        {
            Relevance = Math.Clamp(Relevance(e) ?? 1, 0, 3),
            Project = Key(e, keys),
            Kind = Kind(e),
            Reason = Text(e, "reason") ?? "",
        };
    }

    public static ReadResult? Read(string text, IReadOnlySet<string> keys)
    {
        if (ExtractObject(text) is not { } e || Text(e, "summary") is not { } summary)
        {
            return null;
        }

        var kind = Kind(e);
        return new ReadResult
        {
            Summary = summary,
            Why = Text(e, "why") ?? "",
            Kind = kind,
            Project = Key(e, keys),
            Suggestion = kind == "fyi" ? null : Text(e, "suggestion"),
        };
    }

    public static ReleaseNote? Release(string text)
    {
        if (ExtractObject(text) is not { } e || Text(e, "changes") is not { } changes)
        {
            return null;
        }

        var breaking = Text(e, "breaking")?.ToLowerInvariant();
        return new ReleaseNote(changes, breaking is "yes" or "no" ? breaking : "unknown", Text(e, "evidence"));
    }

    public static ReplyIntent Reply(string text, IReadOnlyCollection<string> planeProjects)
    {
        if (ExtractObject(text) is not { } e || Text(e, "action")?.ToLowerInvariant() is not { } action || !Actions.Contains(action))
        {
            return new ReplyIntent("unclear");
        }

        var named = Text(e, "project");
        var project = named is null ? null : planeProjects.FirstOrDefault(p => string.Equals(p, named, StringComparison.OrdinalIgnoreCase));
        return new ReplyIntent(action, project, project is null ? named : null, Text(e, "idea"), Text(e, "question"));
    }

    private static JsonElement? ExtractObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var element = Json.Parse(text[start..(end + 1)]);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Relevance(JsonElement e)
    {
        if (!e.TryGetProperty("relevance", out var r))
        {
            return null;
        }

        if (r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var n))
        {
            return n;
        }

        return r.ValueKind == JsonValueKind.String && int.TryParse(r.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    /// <summary>A trimmed string property; blank and the literal "null" count as absent.</summary>
    private static string? Text(JsonElement e, string name) =>
        Json.Str(e, name)?.Trim() is { Length: > 0 } s && s != "null" ? s : null;

    private static string Kind(JsonElement e) => Text(e, "kind") is { } k && Kinds.Contains(k) ? k : "fyi";

    private static string? Key(JsonElement e, IReadOnlySet<string> keys) => Text(e, "project") is { } k && keys.Contains(k) ? k : null;
}
