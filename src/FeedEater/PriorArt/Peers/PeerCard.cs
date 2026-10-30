using System.Text;
using System.Text.Json;

namespace FeedEater.PriorArt.Peers;

public sealed record CardClaim(string Argument, string Text, bool Marketing);

public sealed record PeerCardResult(IReadOnlyList<CardClaim> Addresses, IReadOnlyList<CardClaim> Features, IReadOnlyList<CardClaim> Uses);

/// <summary>One cheap call per repo: its README and the top of its file tree in, a card (addresses, features, uses) out.</summary>
public static class PeerCard
{
    private const int MaxPerList = 6;

    public static readonly PeerCardResult Empty = new([], [], []);

    public const string System =
        """
        You read one repository's README and the top of its file tree and write a short card about it: the problems it addresses, the
        features it has, and the approaches it uses. Base every claim only on what the README and file tree say. When the README is
        written to sell the project rather than describe it plainly, still record the claim but mark it marketing.

        Reply with one JSON object and nothing else:
        {"addresses": [{"problem": "<a few words, becomes a slug>", "text": "<one sentence, at most 200 characters>", "marketing": <bool>}],
         "features": [{"slug": "<a few words, becomes a slug>", "text": "<one sentence>", "marketing": <bool>}],
         "uses": [{"approach": "<a few words, becomes a slug>", "text": "<one sentence>", "marketing": <bool>}]}
        At most 6 entries in each list; an empty list when the README gives nothing for it.
        """;

    public static string User(string repo, string readme, IReadOnlyList<string> tree)
    {
        var text = new StringBuilder("Repository: ").Append(repo).Append("\n\nFile tree (top two levels):\n");
        text.AppendJoin('\n', tree);
        return text.Append("\n\nREADME:\n").Append(readme).ToString();
    }

    public static PeerCardResult Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return Empty;
        }

        JsonElement root;
        try
        {
            root = Json.Parse(reply[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return Empty;
        }

        return new PeerCardResult(Claims(root, "addresses", "problem"), Claims(root, "features", "slug"), Claims(root, "uses", "approach"));
    }

    private static IReadOnlyList<CardClaim> Claims(JsonElement root, string field, string argField)
    {
        if (!root.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var claims = new List<CardClaim>();
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var argument = Json.Str(e, argField);
            var text = Json.Str(e, "text");
            if (string.IsNullOrWhiteSpace(argument) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (PriorArtText.Slug(argument) is not { Length: > 0 } slug)
            {
                continue;
            }

            claims.Add(new CardClaim(slug, text.Trim(), e.TryGetProperty("marketing", out var m) && m.ValueKind == JsonValueKind.True));
            if (claims.Count == MaxPerList)
            {
                break;
            }
        }

        return claims;
    }
}
