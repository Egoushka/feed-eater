using System.Net;
using System.Text.RegularExpressions;

namespace FeedEater.Text;

public static partial class HtmlText
{
    public static string ToPlain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var s = ScriptOrStyle().Replace(html, " ");
        s = BlockEnd().Replace(s, "\n");
        s = Tag().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        s = LineEdges().Replace(s, "\n");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex LineEdges();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
