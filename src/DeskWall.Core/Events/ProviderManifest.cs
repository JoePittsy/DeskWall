using System.Text.Json;
using DeskWall.Core.Bindings;
using IoPath = System.IO.Path;

namespace DeskWall.Core.Events;

/// <summary>One field a provider publishes, as its author describes it.</summary>
/// <param name="Path">Dotted path inside the provider, e.g. "data.status".</param>
/// <param name="Type">"text", "number", "bool", "time", "record" or "list". Free text: the
/// binding picker shows it, nothing branches on it.</param>
public sealed record ProviderField(string Path, string Type, string? Example, string? Description);

/// <summary>What observation cannot supply about a provider (spec section 5). Because every
/// provider's last record is remembered, a provider that has run once is already fully bindable,
/// so a manifest exists only for a provider that has never run here, for descriptions and
/// examples per field so the binding picker reads as prose rather than as a dump of last values,
/// and for expectEvery, which turns staleness back on.
/// <para>Where a manifest and an observed record disagree about a field, the observed value wins
/// for rendering and the manifest wins for describing.</para>
/// <para>Read with JsonDocument rather than a deserializer: Core is native AOT, and hand-reading
/// is also what lets an error name the file and the field that caused it.</para></summary>
public sealed class ProviderManifest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<ProviderField> Fields { get; init; } = [];

    /// <summary>How often the producer promises to send. Carried and shown here; enforcing
    /// staleness from it is phase 2.</summary>
    public int? ExpectEverySeconds { get; init; }

    /// <summary>The file this came from - not a field in the file. The designer needs it to tell
    /// a shipped manifest from one of the user's own.</summary>
    public string? Path { get; init; }

    public static ProviderManifest Load(string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException($"{path}: a provider manifest must be a JSON object");

            var name = Text(root, "name");
            if (string.IsNullOrWhiteSpace(name)) throw new FormatException($"{path}: missing \"name\"");
            // The name is the first segment of every binding into this provider.
            if (!BindingParser.IsName(name))
                throw new FormatException($"{path}: \"name\" \"{name}\" is not a valid binding name: a letter or _ then letters, digits, _ or -");
            var description = Text(root, "description");
            if (string.IsNullOrWhiteSpace(description)) throw new FormatException($"{path}: missing \"description\"");

            var fields = new List<ProviderField>();
            if (root.TryGetProperty("fields", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) throw new FormatException($"{path}: a \"fields\" entry is not an object");
                    var fieldPath = Text(f, "path");
                    if (string.IsNullOrWhiteSpace(fieldPath)) throw new FormatException($"{path}: a \"fields\" entry is missing \"path\"");
                    fields.Add(new ProviderField(fieldPath, Text(f, "type") ?? "text", Text(f, "example"), Text(f, "description")));
                }

            int? expect = null;
            if (root.TryGetProperty("expectEverySeconds", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var secs) && secs > 0)
                expect = secs;

            return new ProviderManifest
            {
                Name = name,
                Description = description,
                Fields = fields,
                ExpectEverySeconds = expect,
                Path = path,
            };
        }
    }

    /// <summary>What the designer's "Describe this provider" writes, seeded from the observed
    /// fields. Indented, because a human is meant to open it and add the descriptions.</summary>
    public static string ToJson(ProviderManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("name", m.Name);
            w.WriteString("description", m.Description);
            if (m.ExpectEverySeconds is { } secs) w.WriteNumber("expectEverySeconds", secs);
            w.WriteStartArray("fields");
            foreach (var f in m.Fields)
            {
                w.WriteStartObject();
                w.WriteString("path", f.Path);
                w.WriteString("type", f.Type);
                if (f.Example is not null) w.WriteString("example", f.Example);
                if (f.Description is not null) w.WriteString("description", f.Description);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? Text(JsonElement el, string name)
        => el.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

public static class ProviderCatalog
{
    /// <summary>Shipped beside the exe.</summary>
    public static string ShippedDir => IoPath.Combine(AppContext.BaseDirectory, "providers");

    /// <summary>A user's own manifests, read after the shipped ones so they can override a name.</summary>
    public static string UserDir => Paths.InRuntime("providers");

    /// <summary>Loads every "*.json" in each directory, in the order given, exactly as
    /// WidgetCatalog.Load does: a later directory's manifest with the same provider name replaces
    /// an earlier one but keeps the position the name was first seen at, so a user's override
    /// does not make the list jump around. A directory that does not exist is skipped, not an
    /// error. A malformed file throws, naming itself.
    /// <para>Keyed on the manifest's own "name", not on the file name: the name is what a
    /// provider record and a layout binding use, and that is the thing two manifests can collide
    /// on.</para></summary>
    public static IReadOnlyList<ProviderManifest> Load(params string[] dirs)
    {
        ArgumentNullException.ThrowIfNull(dirs);
        var order = new List<string>();
        var byName = new Dictionary<string, ProviderManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var m = ProviderManifest.Load(file);
                if (!byName.ContainsKey(m.Name)) order.Add(m.Name);
                byName[m.Name] = m;
            }
        }
        return order.Select(n => byName[n]).ToList();
    }
}
