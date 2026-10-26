using System.Text;

namespace FeedEater.Issues;

/// <summary>Text from feeds and the model, made safe for an issue body: no stray formatting, links or @mentions.</summary>
public static class Markdown
{
    private const string Special = "\\`*_[]<>#@|";

    public static string Text(string text)
    {
        var s = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Special.Contains(c))
            {
                s.Append('\\');
            }

            s.Append(c);
        }

        return s.ToString();
    }

    /// <summary>A link: escaped text, and a URL that cannot end the link early.</summary>
    public static string Link(string text, string url) =>
        $"[{Text(text)}]({url.Replace(" ", "%20", StringComparison.Ordinal).Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal)})";
}
