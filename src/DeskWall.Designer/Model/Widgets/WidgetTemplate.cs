using System.Globalization;
using System.IO;
using System.Text.Json;
using DeskWall.Core;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>What a knob offers. <see cref="KnobType.Town"/> is a text box whose value is resolved
/// once to coordinates; the rest are the obvious control.</summary>
public enum KnobType { Number, Text, Choice, Color, Drive, Town }

/// <summary>One dial on a widget's front panel. <paramref name="Sets"/> are the paths written when
/// it changes: <c>components.&lt;id&gt;.&lt;property&gt;</c>, <c>sources.&lt;name&gt;.settings.&lt;key&gt;</c>,
/// either with a trailing <c>:{token}</c> to substitute inside the current string, or with
/// <c>=bind:&lt;binding&gt;</c> to write a binding rather than a literal.</summary>
public sealed record Knob(
    string Id,
    string Label,
    KnobType Type,
    string Default,
    IReadOnlyList<string> Sets,
    IReadOnlyList<string>? Choices,
    double? Min,
    double? Max);

/// <summary>A widget the gallery can offer: a footprint, the components that fill it in template
/// coordinates, the sources they need, and the handful of knobs the owner is allowed to turn.
/// Loaded from <c>widgets/&lt;key&gt;.json</c>.</summary>
public sealed class WidgetTemplate
{
    public required string Name { get; init; }

    /// <summary>The file name without .json. Identifies the template in a layout's widgets record.</summary>
    public string Key { get; init; } = "";

    public required string Description { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>"top" or "bottom": which end of the column the arranger stacks this widget from.</summary>
    public string Anchor { get; init; } = "top";

    public IReadOnlyList<SourceDef> Sources { get; init; } = Array.Empty<SourceDef>();

    /// <summary>Template coordinates: 0,0 is the widget's top-left.</summary>
    public IReadOnlyList<ComponentDef> Components { get; init; } = Array.Empty<ComponentDef>();

    public IReadOnlyList<Knob> Knobs { get; init; } = Array.Empty<Knob>();

    /// <summary>A human sentence shown on the card when the widget needs something the machine may
    /// not have ("Needs an NVIDIA GPU"). Null when it always works.</summary>
    public string? Requires { get; init; }

    public static WidgetTemplate Load(string path)
    {
        var key = Path.GetFileNameWithoutExtension(path);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new FormatException($"{Path.GetFileName(path)}: {ex.Message}", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            var size = Require(root, "size", key);
            if (size.ValueKind != JsonValueKind.Array || size.GetArrayLength() != 2)
                throw new FormatException($"{key}.json: 'size' must be [width, height]");

            // Components and sources are parsed by the layout's own source-generated context, so a
            // template's component objects are exactly a layout's and stay in step with it for free.
            var shell = $$"""
                { "version": 1, "baseImage": "",
                  "sources": {{(root.TryGetProperty("sources", out var s) ? s.GetRawText() : "[]")}},
                  "components": {{Require(root, "components", key).GetRawText()}} }
                """;
            LayoutFile parsed;
            try { parsed = LayoutFile.Parse(shell); }
            catch (JsonException ex) { throw new FormatException($"{key}.json: components: {ex.Message}", ex); }

            return new WidgetTemplate
            {
                Key = key,
                Name = Text(root, "name", key) ?? key,
                Description = Text(root, "description", key) ?? "",
                Width = size[0].GetInt32(),
                Height = size[1].GetInt32(),
                Anchor = Text(root, "anchor", key) ?? "top",
                Requires = Text(root, "requires", key),
                Sources = parsed.Sources,
                Components = parsed.Components,
                Knobs = ReadKnobs(root, key),
            };
        }
    }

    /// <summary>A layout holding just this widget at 0,0 on <paramref name="baseImage"/>, for the
    /// gallery card's real render.</summary>
    public LayoutFile Preview(string baseImage)
    {
        var copy = LayoutFile.Parse(new LayoutFile
        {
            BaseImage = baseImage,
            Sources = Sources.ToList(),
            Components = Components.ToList(),
        }.ToJson());
        copy.BaseFit = Fit.Cover;
        return copy;
    }

    private static JsonElement Require(JsonElement root, string name, string key)
        => root.TryGetProperty(name, out var e) ? e : throw new FormatException($"{key}.json: '{name}' is missing");

    private static string? Text(JsonElement root, string name, string key)
    {
        if (!root.TryGetProperty(name, out var e)) return null;
        if (e.ValueKind != JsonValueKind.String) throw new FormatException($"{key}.json: '{name}' must be a string");
        return e.GetString();
    }

    private static List<Knob> ReadKnobs(JsonElement root, string key)
    {
        var knobs = new List<Knob>();
        if (!root.TryGetProperty("knobs", out var arr) || arr.ValueKind != JsonValueKind.Array) return knobs;
        foreach (var k in arr.EnumerateArray())
        {
            var id = Text(k, "id", key) ?? throw new FormatException($"{key}.json: a knob has no 'id'");
            var typeText = Text(k, "type", key) ?? "text";
            if (!Enum.TryParse<KnobType>(typeText, ignoreCase: true, out var type))
                throw new FormatException($"{key}.json: knob '{id}' has unknown type '{typeText}'");
            knobs.Add(new Knob(
                id,
                Text(k, "label", key) ?? id,
                type,
                DefaultText(k),
                k.TryGetProperty("sets", out var sets) && sets.ValueKind == JsonValueKind.Array
                    ? sets.EnumerateArray().Select(e => e.GetString() ?? "").Where(v => v.Length > 0).ToList()
                    : new List<string>(),
                k.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array
                    ? ch.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : null,
                k.TryGetProperty("min", out var min) && min.ValueKind == JsonValueKind.Number ? min.GetDouble() : null,
                k.TryGetProperty("max", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetDouble() : null));
        }
        if (knobs.Count > 5) throw new FormatException($"{key}.json: {knobs.Count} knobs; a widget may expose at most five");
        return knobs;
    }

    /// <summary>A default may be written as a number or a string; the knob carries text either way.</summary>
    private static string DefaultText(JsonElement k)
    {
        if (!k.TryGetProperty("default", out var d)) return "";
        return d.ValueKind switch
        {
            JsonValueKind.String => d.GetString() ?? "",
            JsonValueKind.Number => d.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }
}
