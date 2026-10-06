using System.Collections;
using System.Globalization;
using System.Reflection;
using Npgsql;

namespace FeedEater.Setup;

/// <summary>Marks a setting whose value must never be printed or logged.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>One leaf setting: its configuration key, the property it binds to and its current value.</summary>
internal sealed record ConfigEntry(string Key, PropertyInfo Property, object? Value);

/// <summary>
/// The effective configuration as sorted <c>key=value</c> lines with secrets masked: what the options hold after the environment
/// and files are applied, defaults included. Two runs can be diffed to show that a change moved no setting.
/// </summary>
public static class ConfigPrinter
{
    private const string Masked = "***";

    public static IReadOnlyList<string> Lines(IConfiguration configuration)
    {
        var options = configuration.GetSection(FeedEaterOptions.Section).Get<FeedEaterOptions>() ?? new FeedEaterOptions();
        var lines = Entries(options, FeedEaterOptions.Section, schema: false).Select(e => $"{e.Key}={Display(e)}").ToList();
        lines.Add($"ConnectionStrings:FeedEater={MaskConnection(configuration.GetConnectionString("FeedEater") ?? "")}");
        lines.Add($"Mcp:Token={Mask(configuration["Mcp:Token"])}");
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>
    /// Every settable leaf under <paramref name="options"/>. A map (such as the model prices) lists its real entries, or with
    /// <paramref name="schema"/> one placeholder entry for the shape of its values.
    /// </summary>
    internal static IEnumerable<ConfigEntry> Entries(object options, string prefix, bool schema)
    {
        foreach (var p in options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var key = $"{prefix}:{p.Name}";
            var value = p.GetValue(options);
            if (value is IDictionary map)
            {
                var items = new List<(string, object)>();
                foreach (DictionaryEntry d in map)
                {
                    items.Add(($"{key}:{d.Key}", d.Value!));
                }

                if (schema && items.Count == 0)
                {
                    items.Add(($"{key}:<name>", Activator.CreateInstance(p.PropertyType.GetGenericArguments()[1])!));
                }

                foreach (var (itemKey, item) in items)
                {
                    foreach (var entry in Entries(item, itemKey, schema))
                    {
                        yield return entry;
                    }
                }
            }
            else if (p.PropertyType.IsClass && p.PropertyType != typeof(string) && !p.PropertyType.IsArray)
            {
                foreach (var entry in Entries(value!, key, schema))
                {
                    yield return entry;
                }
            }
            else
            {
                yield return new ConfigEntry(key, p, value);
            }
        }
    }

    /// <summary>The value as printed: a secret is masked, a URL loses its credentials and query.</summary>
    internal static string Display(ConfigEntry e) =>
        e.Property.IsDefined(typeof(SecretAttribute)) ? Mask(e.Value as string) : Redact(Format(e.Value));

    internal static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        string s => s,
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        Array a => string.Join(',', a.Cast<object?>().Select(Format)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Mask(string? secret) => string.IsNullOrEmpty(secret) ? "" : Masked;

    private static string Redact(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || (uri.UserInfo.Length == 0 && uri.Query.Length == 0))
        {
            return value;
        }

        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped) + (uri.Query.Length > 0 ? "?" + Masked : "");
    }

    /// <summary>Parsed by Npgsql, so a quoted password that holds a <c>;</c> is masked whole. A string it cannot parse is masked entirely: there is no telling where its password is.</summary>
    private static string MaskConnection(string connection)
    {
        if (connection.Length == 0)
        {
            return "";
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connection);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = Masked;
            }

            if (!string.IsNullOrEmpty(builder.SslPassword))
            {
                builder.SslPassword = Masked;
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return Masked;
        }
    }
}
