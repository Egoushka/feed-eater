using System.Text.Json;

namespace FeedEater.Profiles;

public sealed record ProfileEntry(string Key, string Description, string? Plane = null);

/// <summary>Hand-written description of the reader, their projects (with optional Plane identifiers) and their topics.</summary>
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

    /// <summary>
    /// The file at <paramref name="path"/>, or the neutral example built into the app when there is no such file, so a first start
    /// works. A file that exists but is invalid still throws: that is a mistake to fix, not a reason to guess. So does a directory at the path,
    /// which is what Docker creates when a bind-mounted file is missing on the host.
    /// </summary>
    public static (ProfileFile File, bool IsExample) LoadOrExample(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("the profile path is a directory, not a file (Docker makes one when a bind-mounted file is missing on the host)");
        }

        return File.Exists(path) ? (Load(path), false) : (Example(), true);
    }

    public static string ExampleNote(string path) => $"Using the example interests; edit {path}";

    private static ProfileFile Example()
    {
        using var stream = typeof(ProfileFile).Assembly.GetManifestResourceStream("FeedEater.profile.example.json")!;
        return Parse(new StreamReader(stream).ReadToEnd());
    }

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
