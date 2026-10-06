using System.Globalization;
using System.Text.RegularExpressions;

namespace FeedEater.Watch;

public static partial class Versions
{
    /// <summary>The first dotted number in the text ("v2.3.0", "version-6.7.6", "0.60.8-alpine", "2.0.3 (latest@2026-09-08)"); null when there is none.</summary>
    public static int[]? Parse(string? text) =>
        text is not null && Dotted().Match(text) is { Success: true } m
            ? m.Value.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray()
            : null;

    public static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    /// <summary>A release candidate, beta, nightly and the like: never announced as "out".</summary>
    public static bool IsPrerelease(string tag) => Pre().IsMatch(tag);

    public static string Text(int[] v) => string.Join('.', v);

    [GeneratedRegex(@"\d+(?:\.\d+)+", RegexOptions.None, 250)]
    private static partial Regex Dotted();

    [GeneratedRegex(@"(?:^|[-_.+])(rc|alpha|beta|pre|preview|dev|snapshot|nightly|canary|next)\d*(?:$|[-_.+\d])", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Pre();
}
