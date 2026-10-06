using System.Text.RegularExpressions;

namespace FeedEater.Watch;

public sealed record Pinned(string Service, string? Version, string Image);

/// <summary>Reads the rows of a PINS.md: "| service | version | `image:tag@sha256:...` |".</summary>
public static partial class PinsParser
{
    public static IReadOnlyList<Pinned> Parse(string text)
    {
        var pins = new List<Pinned>();
        foreach (var line in text.Split('\n'))
        {
            if (Row().Match(line) is not { Success: true } m)
            {
                continue;
            }

            var image = Image(m.Groups[3].Value);
            if (image is null || m.Groups[1].Value.Trim() is "Service" or "Image")
            {
                continue;
            }

            var version = Versions.Parse(m.Groups[2].Value);
            pins.Add(new Pinned(m.Groups[1].Value.Trim(), version is null ? null : Versions.Text(version), image));
        }

        return pins;
    }

    /// <summary>The repository without tag or digest, or null when the code span is not an image (a path, a volume).</summary>
    private static string? Image(string span)
    {
        var image = span.Split('@')[0];
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        if (colon > slash)
        {
            image = image[..colon];
        }

        image = image.StartsWith("docker.io/", StringComparison.Ordinal) ? image["docker.io/".Length..] : image;
        return ImageName().IsMatch(image) && !image.EndsWith("_data", StringComparison.Ordinal) ? image : null;
    }

    [GeneratedRegex(@"^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*`([^`]+)`", RegexOptions.None, 250)]
    private static partial Regex Row();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*(/[A-Za-z0-9._-]+)*$", RegexOptions.None, 250)]
    private static partial Regex ImageName();
}
