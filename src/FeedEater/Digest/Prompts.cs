using FeedEater.Profiles;
using FeedEater.Storage;

namespace FeedEater.Digest;

public static class Prompts
{
    public static (string System, string User) Triage(
        string about, IReadOnlyList<Profile> profiles, string title, string feed, string text, int maxChars) => (
        $$"""
        You rank news items for one reader. About him: {{about}}
        Reply with one JSON object and nothing else:
        {"relevance": 0-3, "project": "<key>" or null, "kind": "improve" | "new" | "fyi", "reason": "<at most 20 words>"}
        relevance: 3 = he should act on it this week, 2 = worth reading today, 1 = marginal, 0 = noise.
        kind: improve = changes one of his projects; new = something he could build or adopt separately; fyi = worth knowing only.
        project: one of the keys listed, or null.
        """,
        $"""
        Projects and topics:
        {List(profiles)}

        Item
        Title: {title}
        Feed: {feed}
        Text: {Clip(text, maxChars)}
        """);

    public static (string System, string User) Read(
        string about, IReadOnlyList<Profile> profiles, Profile? match, string title, string url, string feed, string text, int maxChars) => (
        $$"""
        You read one article for one reader and tell him what matters. About him: {{about}}
        Reply with one JSON object and nothing else:
        {"summary": "<at most 2 sentences: what is new>", "why": "<1 sentence: why it matters to him>", "kind": "improve" | "new" | "fyi", "project": "<key>" or null, "suggestion": "<at most 2 sentences: one concrete action>" or null}
        Write in English whatever the article's language. Plain words, no hype; if it is a minor release, say so.
        Give a suggestion only when kind is improve (an action in that project) or new (something to build or adopt).
        project: one of the keys listed, or null.
        """,
        $"""
        Best match: {(match is null ? "none" : $"{match.Key}: {match.Description}")}

        Projects and topics:
        {List(profiles)}

        Article
        Title: {title}
        URL: {url}
        Feed: {feed}

        {Clip(text, maxChars)}
        """);

    private static string List(IReadOnlyList<Profile> profiles) =>
        string.Join('\n', profiles.Select(p => $"- {p.Key} ({p.Kind}): {ProfileFile.OneLiner(p.Description)}"));

    private static string Clip(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars];
}
