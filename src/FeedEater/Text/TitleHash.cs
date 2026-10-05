using System.Security.Cryptography;
using System.Text;

namespace FeedEater.Text;

public static class TitleHash
{
    private const int MinLetters = 8;

    /// <summary>Lowercase letters and digits only, hashed. Empty below 8 of them: "News" must not match every other "News".</summary>
    public static string Of(string title)
    {
        var letters = new StringBuilder(title.Length);
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                letters.Append(ch);
            }
        }

        return letters.Length < MinLetters
            ? ""
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(letters.ToString())))[..16];
    }
}
