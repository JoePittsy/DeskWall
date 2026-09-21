using System.Globalization;
using System.Text.Json;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;

namespace DeskWall.Core.Events;

/// <summary>The last record of every provider, on disk. Spec section 3: the pipe belongs to the
/// daemon and the designer is a separate process, so a provider the designer has never been told
/// about is one it can never offer a binding for. This file is the seam - the daemon writes it,
/// the designer reads and watches it - and it is also what makes a pushed widget survive sign-in.
/// <para>Hand-written with JsonDocument and Utf8JsonWriter: Core is native AOT and reflection
/// based serialization is not available to it.</para></summary>
public static class EventStore
{
    private const int Version = 1;

    public static string Path => Paths.InRuntime("events.json");

    /// <summary>Never throws; missing or corrupt gives empty. A record whose own shape is wrong
    /// is skipped and the rest are kept: one bad entry must not cost a user every provider they
    /// have.</summary>
    public static IReadOnlyList<ProviderRecord> Load()
    {
        string text;
        try
        {
            if (!File.Exists(Path)) return [];
            text = File.ReadAllText(Path);
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("providers", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            var list = new List<ProviderRecord>();
            foreach (var el in arr.EnumerateArray())
                if (ReadRecord(el) is { } r) list.Add(r);
            return list;
        }
        catch (JsonException) { return []; }
    }

    public static void Save(IEnumerable<ProviderRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var path = Path;
        // Same directory, so the move is a rename inside one volume and cannot half-happen. A
        // torn events.json would cost the user every remembered provider, and the daemon writes
        // this one every few seconds.
        var tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            using (var stream = File.Create(tmp))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", Version);
                w.WriteStartArray("providers");
                foreach (var r in records) WriteRecord(w, r);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // A failed write must not leave the scratch file behind for the next Save to trip on.
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
    }

    private static ProviderRecord? ReadRecord(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var name = Text(el, "name");
        if (name is null) return null;
        var data = el.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? JsonValues.From(d)
            : new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase));
        if (Time(el, "receivedAt") is not { } received) return null;
        return new ProviderRecord(name, data, Text(el, "type"), Text(el, "subject"), Text(el, "id"), Time(el, "sentAt"), received);
    }

    private static void WriteRecord(Utf8JsonWriter w, ProviderRecord r)
    {
        w.WriteStartObject();
        w.WriteString("name", r.Name);
        w.WritePropertyName("data");
        WriteValue(w, r.Data);
        if (r.Type is not null) w.WriteString("type", r.Type);
        if (r.Subject is not null) w.WriteString("subject", r.Subject);
        if (r.Id is not null) w.WriteString("id", r.Id);
        if (r.SentAt is { } sent) w.WriteString("sentAt", sent.ToString("o", CultureInfo.InvariantCulture));
        w.WriteString("receivedAt", r.ReceivedAt.ToString("o", CultureInfo.InvariantCulture));
        w.WriteEndObject();
    }

    /// <summary>The payload goes back out as plain JSON and comes back in through JsonValues, the
    /// same mapping it arrived by, so nothing a producer can send over the pipe is lost. A
    /// TimeValue or ImageValue - which only an in-process producer could put in a payload, and
    /// none does - returns as text; the alternative is a tagged encoding that JsonValues would
    /// then have to learn to read, which is two mappings where there is one.</summary>
    private static void WriteValue(Utf8JsonWriter w, Value v)
    {
        switch (v)
        {
            case TextValue t: w.WriteStringValue(t.Text); break;
            // A non-finite double is not JSON and would throw here. Null instead: JsonValues drops
            // a null, so the field simply stops being published rather than the save failing.
            case NumberValue n: if (double.IsFinite(n.Number)) w.WriteNumberValue(n.Number); else w.WriteNullValue(); break;
            case BoolValue b: w.WriteBooleanValue(b.Flag); break;
            case TimeValue t: w.WriteStringValue(t.Time.ToString("o", CultureInfo.InvariantCulture)); break;
            case ImageValue i: w.WriteStringValue(i.Path); break;
            case RecordValue r:
                w.WriteStartObject();
                foreach (var (k, child) in r.Fields) { w.WritePropertyName(k); WriteValue(w, child); }
                w.WriteEndObject();
                break;
            case ListValue l:
                w.WriteStartArray();
                foreach (var item in l.Items) WriteValue(w, item);
                w.WriteEndArray();
                break;
            default: w.WriteNullValue(); break;
        }
    }

    private static string? Text(JsonElement el, string name)
        => el.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static DateTimeOffset? Time(JsonElement el, string name)
        => Text(el, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
