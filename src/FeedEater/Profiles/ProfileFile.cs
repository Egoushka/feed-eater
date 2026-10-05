using System.Text.Json;

namespace FeedEater.Profiles;

public sealed record ProfileEntry(string Key, string Description, string? Plane = null);

/// <summary>Hand-written description of Yehor, his projects (with Plane identifiers) and his topics.</summary>
public sealed record ProfileFile(string About, IReadOnlyList<ProfileEntry> Projects, IReadOnlyList<ProfileEntry> Topics)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static ProfileFile Load(string path) => Parse(File.ReadAllText(path));

    public static ProfileFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ProfileFile>(json, Options) ?? throw new InvalidDataException("the profile file is empty");
        var entries = file.Projects.Concat(file.Topics).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("the profile file has no projects or topics");
        }

        if (entries.Any(e => string.IsNullOrWhiteSpace(e.Key) || string.IsNullOrWhiteSpace(e.Description)))
        {
            throw new InvalidDataException("every profile entry needs a key and a description");
        }

        var duplicate = entries.GroupBy(e => e.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? file : throw new InvalidDataException($"profile key '{duplicate.Key}' appears twice");
    }

    /// <summary>The first sentence, at most 160 characters: what prompts list for each key.</summary>
    public static string OneLiner(string description)
    {
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end < 0 ? description : description[..(end + 1)];
        return sentence.Length <= 160 ? sentence : sentence[..157] + "...";
    }
}
