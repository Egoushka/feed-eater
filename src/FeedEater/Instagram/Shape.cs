using System.Text;
using System.Text.Json;

namespace FeedEater.Instagram;

/// <summary>
/// The structure of a JSON value as key names and value types, never the values: an export is the owner's private data and this output is meant to be pasted
/// into a spec. Several array items are merged, so a key that only some items have shows as optional.
/// </summary>
public sealed class Shape
{
    public const int MaxDepth = 7;
    public const int MaxKeys = 40;
    public const int SampleItems = 5;
    public const int MaxLabels = 25;

    private readonly SortedSet<string> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Shape> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _labels = new(StringComparer.Ordinal);
    private Shape? _item;
    private int _objects;
    private int _maxCount;

    public static Shape Of(JsonElement root, bool labels)
    {
        var shape = new Shape();
        shape.Add(root, 0, labels);
        return shape;
    }

    public string Type => string.Join("|", _types);

    public int Count => _maxCount;

    public void Add(JsonElement e, int depth, bool labels)
    {
        _types.Add(e.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.Null => "null",
            _ => "bool",
        });
        if (depth >= MaxDepth)
        {
            return;
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            _maxCount = Math.Max(_maxCount, e.GetArrayLength());
            _item ??= new Shape();
            foreach (var item in e.EnumerateArray().Take(SampleItems))
            {
                _item.Add(item, depth + 1, labels);
            }
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            _objects++;
            foreach (var p in e.EnumerateObject().Take(MaxKeys))
            {
                if (!_children.TryGetValue(p.Name, out var child))
                {
                    _children[p.Name] = child = new Shape();
                }

                _seen[p.Name] = _seen.GetValueOrDefault(p.Name) + 1;
                child.Add(p.Value, depth + 1, labels);
                if (labels && p.Name == "label" && p.Value.ValueKind == JsonValueKind.String && _labels.Count < MaxLabels && p.Value.GetString() is { Length: > 0 and <= 60 } label)
                {
                    _labels.Add(label);
                }
            }
        }
    }

    public string Render(int indent = 0)
    {
        var text = new StringBuilder();
        Append(text, indent);
        return text.ToString();
    }

    private void Append(StringBuilder text, int indent)
    {
        var pad = new string(' ', indent);
        if (_labels.Count > 0)
        {
            text.Append(pad).Append("label values (field names, shown because of --labels): ").AppendJoin("; ", _labels).Append('\n');
        }

        foreach (var (name, child) in _children)
        {
            var optional = _seen[name] < _objects ? "?" : "";
            text.Append(pad).Append(name).Append(optional).Append(": ").Append(child.Header()).Append('\n');
            child.Append(text, indent + 2);
        }

        _item?.AppendItem(text, indent + 2);
    }

    private void AppendItem(StringBuilder text, int indent)
    {
        if (_children.Count == 0 && _item is null)
        {
            return;
        }

        Append(text, indent);
    }

    private string Header() => _types.Contains("array") ? $"{Type} (up to {_maxCount} items)" : Type;
}
