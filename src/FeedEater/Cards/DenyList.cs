namespace FeedEater.Cards;

/// <summary>
/// Terms that must never be read into the board, queried outside or logged: literal strings, one per line, case-insensitive,
/// lines under 3 characters and <c>#</c> comments skipped (the format of the workspace leak-guard's terms file).
/// </summary>
public sealed class DenyList(IReadOnlyList<string> terms)
{
    public static readonly DenyList Empty = new([]);

    public int Count => terms.Count;

    /// <summary>The list in <paramref name="path"/>, or null when it is not set, missing or unreadable: callers that need it fail closed.</summary>
    public static DenyList? Load(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        try
        {
            return new DenyList(File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length >= 3 && !l.StartsWith('#')).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Matches(params string?[] texts) =>
        texts.Any(t => t is not null && terms.Any(term => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
}
