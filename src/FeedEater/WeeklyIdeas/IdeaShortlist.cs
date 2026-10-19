using System.Globalization;
using System.Text;
using System.Text.Json;
using FeedEater.Digest;

namespace FeedEater.WeeklyIdeas;

/// <summary>Something that might become an idea: an item you liked, saved or that was marked improve, or a Karakeep bookmark.</summary>
public sealed record Lead(string Signal, string Title, string Url, string Detail);

/// <summary>A Plane project an idea can go to, with what the model needs to choose it.</summary>
public sealed record Target(string Identifier, string Key, string Description);

public sealed record ShortlistEntry(int Lead, string Idea, string Project, string Where, string Effort, string Why);

/// <summary>The prompt that ranks the week's leads into ideas, and the lenient reading of its answer.</summary>
public static class IdeaShortlist
{
    private const int MaxBacklogChars = 80_000;
    private static readonly string[] Efforts = ["S", "M", "L"];

    public static string System(int max) => $$"""
        You turn one week of the owner's reading signals into a short list of concrete work for his own projects.
        You get numbered leads (an item he liked or saved, an item marked "improve", or a bookmark), the Plane projects an idea can go to,
        and everything that is already planned or filed.

        Pick at most {{max}} leads that suggest a specific change the owner could make to one of his projects or workflows, best first.
        Skip news, opinions and anything with no action. Skip a lead whose idea repeats a row of the backlog, a Plane item or an earlier
        shortlist idea, even if the wording differs. Fewer ideas, or none, beats a weak one.

        Reply with one JSON object and nothing else:
        {"ideas": [{"lead": <number>, "idea": "<imperative, at most 90 characters>", "project": "<Plane identifier from the list>", "where": "<repo or workflow it changes>", "effort": "S" | "M" | "L", "why": "<at most 25 words: what it fixes or gains>"}]}
        Effort: S under an hour, M up to a day, L longer.
        """;

    public static string User(IReadOnlyList<Lead> leads, IReadOnlyList<Target> targets, IReadOnlyList<string> planeTitles, IReadOnlyList<string> earlier, string? backlog)
    {
        var text = new StringBuilder("Plane projects:\n");
        foreach (var t in targets)
        {
            text.Append(CultureInfo.InvariantCulture, $"- {t.Identifier} ({t.Key}): {Clip(t.Description, 200)}\n");
        }

        text.Append("\nLeads:\n");
        for (var i = 0; i < leads.Count; i++)
        {
            var l = leads[i];
            text.Append(CultureInfo.InvariantCulture, $"{i + 1}. [{l.Signal}] {l.Title} <{l.Url}>");
            if (l.Detail.Length > 0)
            {
                text.Append(" — ").Append(Clip(l.Detail, 400));
            }

            text.Append('\n');
        }

        text.Append("\nAlready in Plane (latest per project):\n");
        text.AppendJoin('\n', planeTitles.Select(t => "- " + t));
        text.Append("\n\nFiled by earlier shortlists:\n");
        text.AppendJoin('\n', earlier.Select(t => "- " + t));
        text.Append("\n\nBacklog:\n").Append(backlog is null ? "(not available)" : Clip(backlog, MaxBacklogChars));
        return text.ToString();
    }

    /// <summary>Entries with a known lead number and an idea; a project that is not allowed becomes <paramref name="fallback"/>. A lead is used once.</summary>
    public static IReadOnlyList<ShortlistEntry> Parse(string reply, int leadCount, IReadOnlyCollection<string> projects, string fallback)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return [];
        }

        JsonElement root;
        try
        {
            root = Json.Parse(reply[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return [];
        }

        if (!root.TryGetProperty("ideas", out var ideas) || ideas.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var used = new HashSet<int>();
        var entries = new List<ShortlistEntry>();
        foreach (var e in ideas.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("lead", out var n) || !n.TryGetInt32(out var lead)
                || lead < 1 || lead > leadCount || Json.Str(e, "idea") is not { Length: > 0 } idea || !used.Add(lead))
            {
                continue;
            }

            var named = Json.Str(e, "project");
            var project = projects.FirstOrDefault(p => string.Equals(p, named, StringComparison.OrdinalIgnoreCase)) ?? fallback;
            var effort = Json.Str(e, "effort")?.ToUpperInvariant();
            entries.Add(new ShortlistEntry(lead - 1, idea.Trim(), project, Json.Str(e, "where") ?? "", Efforts.Contains(effort) ? effort! : "M", Json.Str(e, "why") ?? ""));
        }

        return entries;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
