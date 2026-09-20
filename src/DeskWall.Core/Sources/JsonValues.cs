using System.Text.Json;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Any JSON document to the Value tree. Objects -> RecordValue; arrays of objects -> ListValue
/// with KeyField = the first of ("id","appid","key","letter","name") present in every item, else null;
/// arrays of scalars -> ListValue of { "value": scalar }; numbers -> NumberValue; strings -> TextValue;
/// bool -> BoolValue; null -> omitted. A top-level array becomes { "items": [...] }.
/// unixTimeFields: field names whose numbers are Unix seconds and become TimeValue (local).</summary>
public static class JsonValues
{
    private static readonly string[] KeyCandidates = ["id", "appid", "key", "letter", "name"];   // identifiers before display names

    public static RecordValue Parse(string json, IReadOnlySet<string>? unixTimeFields = null)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return From(doc.RootElement, unixTimeFields);
    }

    public static RecordValue From(JsonElement root, IReadOnlySet<string>? unixTimeFields = null)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { ["items"] = List(root, unixTimeFields) });
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("JSON root must be an object or an array");
        return Record(root, unixTimeFields);
    }

    private static RecordValue Record(JsonElement obj, IReadOnlySet<string>? unix)
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
        {
            var v = Convert(p.Value, p.Name, unix);
            if (v is not null) d[p.Name] = v;
        }
        return new RecordValue(d);
    }

    private static ListValue List(JsonElement arr, IReadOnlySet<string>? unix)
    {
        var items = new List<RecordValue>();
        var allObjects = true;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object) items.Add(Record(e, unix));
            else
            {
                allObjects = false;
                var v = Convert(e, "value", unix);
                var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                if (v is not null) d["value"] = v;
                items.Add(new RecordValue(d));
            }
        }
        string? key = null;
        if (allObjects && items.Count > 0)
            key = KeyCandidates.FirstOrDefault(k => items.All(i => i.Get(k) is TextValue or NumberValue));
        return new ListValue(items, key);
    }

    private static Value? Convert(JsonElement e, string name, IReadOnlySet<string>? unix) => e.ValueKind switch
    {
        JsonValueKind.Object => Record(e, unix),
        JsonValueKind.Array => List(e, unix),
        JsonValueKind.String => new TextValue(e.GetString()!),
        JsonValueKind.Number when unix is not null && unix.Contains(name) && e.TryGetInt64(out var secs) => new TimeValue(DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime()),
        JsonValueKind.Number => new NumberValue(e.GetDouble()),
        JsonValueKind.True => new BoolValue(true),
        JsonValueKind.False => new BoolValue(false),
        _ => null,
    };
}
