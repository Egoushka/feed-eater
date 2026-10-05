using System.Text.Json;

namespace FeedEater;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>The string property <paramref name="name"/> of an object, or null when absent or not a string.</summary>
    public static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
