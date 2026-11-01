using System.Text.RegularExpressions;

namespace FeedEater.PriorArt.Peers;

/// <summary>What is wrong with the peer set file: the caller fixes the file, nothing was harvested.</summary>
public sealed class PeerSetFormatException(string message) : Exception(message);

/// <summary>One peer set: a repo plus its public peers, each <c>owner/name</c>.</summary>
public sealed record PeerSet(string Repo, IReadOnlyList<string> Peers)
{
    /// <summary>The repo itself and every peer, own repo first, each once.</summary>
    public IReadOnlyList<string> All => new[] { Repo }.Concat(Peers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// A minimal reader for the peer set file's own shape: a <c>repo:</c> scalar and a <c>peers:</c> block list of <c>owner/name</c>
/// strings. Not a general YAML parser.
/// </summary>
public static partial class PeerSetParser
{
    [GeneratedRegex(@"^[\w.-]+/[\w.-]+$")]
    private static partial Regex RepoName();

    public static PeerSet Parse(string yaml)
    {
        string? repo = null;
        var peers = new List<string>();
        var inPeers = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('-'))
            {
                if (inPeers && Unquote(trimmed[1..].Trim()) is { Length: > 0 } peer)
                {
                    peers.Add(Require(peer, "a peer"));
                }

                continue;
            }

            inPeers = false;
            var colon = trimmed.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            if (string.Equals(key, "repo", StringComparison.OrdinalIgnoreCase))
            {
                repo = Require(Unquote(value), "repo");
            }
            else if (string.Equals(key, "peers", StringComparison.OrdinalIgnoreCase))
            {
                inPeers = true;
            }
        }

        return repo is null ? throw new PeerSetFormatException("the peer set has no repo.") : new PeerSet(repo, peers);
    }

    private static string Require(string value, string what) => RepoName().IsMatch(value) ? value : throw new PeerSetFormatException($"{what} is owner/name, not '{value}'.");

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')) ? value[1..^1] : value;
}
