using System.Text.Json;

namespace FeedEater.Swipe;

public sealed record CategoryEntry(string Key, string Description, double? Share = null, IReadOnlyList<string>? Events = null, bool Seed = false);

/// <summary>
/// What counts as a good use of attention, in the owner's words: one entry per category. A missing file is no categories (the feed
/// then runs on one pool); a file that exists but is invalid throws, like <c>profile.json</c>.
/// </summary>
public sealed record CategoriesFile(IReadOnlyList<CategoryEntry> Categories)
{
    /// <summary>The events a category may list as its yield; the default for a category that lists none is all of them.</summary>
    public static readonly IReadOnlySet<string> YieldEvents = new HashSet<string>(["idea", "share", "save", "recall", "like", "open"], StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static CategoriesFile None { get; } = new([]);

    public static CategoriesFile LoadOrNone(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("the categories path is a directory, not a file (Docker makes one when a bind-mounted file is missing on the host)");
        }

        return File.Exists(path) ? Parse(File.ReadAllText(path)) : None;
    }

    public static CategoriesFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<CategoriesFile>(json, Options) ?? throw new InvalidDataException("the categories file is empty");
        if (file.Categories.Any(c => string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Description)))
        {
            throw new InvalidDataException("every category needs a key and a description");
        }

        var duplicate = file.Categories.GroupBy(c => c.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"category key '{duplicate.Key}' appears twice");
        }

        if (file.Categories.FirstOrDefault(c => c.Share is < 0 or > 1) is { } badShare)
        {
            throw new InvalidDataException($"the share of '{badShare.Key}' must be between 0 and 1");
        }

        if (file.Categories.Sum(c => c.Share ?? 0) > 1.0001)
        {
            throw new InvalidDataException("the category shares add up to more than 1");
        }

        foreach (var c in file.Categories)
        {
            if (c.Events?.FirstOrDefault(e => !YieldEvents.Contains(e)) is { } unknown)
            {
                throw new InvalidDataException($"category '{c.Key}': '{unknown}' is not an event ({string.Join(", ", YieldEvents.Order())})");
            }
        }

        return file;
    }
}
