using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>
/// Writing a widget template back to <c>widgets/&lt;key&gt;.json</c>.
/// <para>
/// The components and sources go through <see cref="LayoutFile.ToJson"/> and are lifted out of the
/// result as JSON nodes rather than being written by hand. There is exactly one component
/// serializer in this codebase, it is source-generated in Core, and a second one here would be a
/// second place for a new property to be forgotten.
/// </para>
/// <para>
/// The contract is the round trip: <see cref="WidgetTemplate.Load"/> of what this writes is the
/// template that went in. <c>WidgetTemplateWriterTests</c> asserts it for every shipped widget.
/// </para>
/// </summary>
public static class WidgetTemplateWriter
{
    /// <summary>The doctrine limit <see cref="WidgetTemplate.Load"/> enforces on the way back in.
    /// Refused here too, with a sentence rather than a FormatException on the next launch.</summary>
    public const int MaxKnobs = 5;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string ToJson(WidgetTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var layout = new LayoutFile
        {
            BaseImage = "",
            Sources = template.Sources.ToList(),
            Components = template.Components.ToList(),
        };
        var encoded = JsonNode.Parse(layout.ToJson())!.AsObject();

        var root = new JsonObject
        {
            ["version"] = 1,
            ["name"] = template.Name,
            ["description"] = template.Description,
            ["size"] = new JsonArray(template.Width, template.Height),
        };
        // Both omitted at their default so a plain widget's file has nothing in it that is not
        // about the widget, and a diff of two of them is about what differs.
        if (!string.Equals(template.Anchor, "top", StringComparison.Ordinal)) root["anchor"] = template.Anchor;
        if (template.Requires is not null) root["requires"] = template.Requires;

        root["sources"] = encoded["sources"]?.DeepClone() ?? new JsonArray();
        root["components"] = encoded["components"]?.DeepClone() ?? new JsonArray();
        root["knobs"] = Knobs(template.Knobs);

        return root.ToJsonString(Indented);
    }

    private static JsonArray Knobs(IReadOnlyList<Knob> knobs)
    {
        var array = new JsonArray();
        foreach (var k in knobs)
        {
            var o = new JsonObject
            {
                ["id"] = k.Id,
                ["label"] = k.Label,
                ["type"] = k.Type.ToString().ToLowerInvariant(),
                ["default"] = k.Default,
            };
            if (k.Choices is { } choices) o["choices"] = Strings(choices);
            o["sets"] = Strings(k.Sets);
            if (k.Min is { } min) o["min"] = min;
            if (k.Max is { } max) o["max"] = max;
            array.Add(o);
        }
        return array;
    }

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values) array.Add(v);
        return array;
    }

    /// <summary>Write the document into <paramref name="userDir"/> and return the file it landed
    /// in. Refuses rather than writes when the result would not load, or would silently shadow a
    /// shipped widget. A rename writes the new file first and deletes the old one after, so a
    /// failure leaves the widget where it was.</summary>
    /// <param name="shippedKeys">the keys of the templates that ship beside the exe. A user file
    /// of the same key overrides one in the catalog, which is fine when that is what was opened
    /// and a trap when it is a new widget that happens to share a name.</param>
    public static string Save(WidgetDocument document, IReadOnlyCollection<string> shippedKeys, string userDir)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shippedKeys);

        var template = document.ToTemplate();
        if (string.IsNullOrWhiteSpace(template.Name)) throw new InvalidOperationException("A widget needs a name.");
        if (string.IsNullOrWhiteSpace(template.Description))
            throw new InvalidOperationException("A widget needs a description: it is all the gallery card has to go on.");
        if (template.Knobs.Count > MaxKnobs)
            throw new InvalidOperationException($"A widget can have at most {MaxKnobs} adjustable settings; this one has {template.Knobs.Count}.");

        var key = document.Key;
        var renamedOrNew = !string.Equals(key, document.EditingKey, StringComparison.OrdinalIgnoreCase);
        if (renamedOrNew && shippedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A shipped widget is already called '{template.Name}'. Pick another name.");

        Directory.CreateDirectory(userDir);
        var path = Path.Combine(userDir, key + ".json");
        File.WriteAllText(path, ToJson(template));

        var previous = document.Path;
        if (renamedOrNew && previous is not null && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
        {
            // Best effort: the new file is already on disk, and a locked old one is a stray
            // template in the gallery, not a lost widget.
            try { File.Delete(previous); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        document.Path = path;
        document.EditingKey = key;
        return path;
    }
}
