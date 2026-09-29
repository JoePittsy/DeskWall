using System.Text.Json;
using System.Text.RegularExpressions;
using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

public enum KnobType { Number, Text, Choice, Color, Drive, Town }

/// <summary>One control in a widget's knobs panel. See <c>docs/layout-format.md</c> "Widgets" for
/// the <see cref="Sets"/> grammar and the composite-value convention some knobs need.</summary>
public sealed record Knob(string Id, string Label, KnobType Type, string Default, IReadOnlyList<string> Sets, IReadOnlyList<string>? Choices, double? Min, double? Max);

/// <summary>A widget loaded from <c>widgets/&lt;key&gt;.json</c>: sources, components in
/// widget-local coordinates (the widget's own top-left is (0, 0)) and up to five knobs. Never
/// mutated once loaded. Same public shape as the designer's
/// <c>DeskWall.Designer.Model.Widgets.WidgetTemplate</c>, which it replaces in Task 2.1.</summary>
public sealed partial class WidgetTemplate
{
    public required string Name { get; init; }
    /// <summary>The file name without ".json" -- not a field in the file.</summary>
    public string Key { get; init; } = "";
    /// <summary>The file this was loaded from -- not a field in the file either.</summary>
    public string? Path { get; init; }
    /// <summary>Whether a shipped widget of the same key is shadowed by this one. Set by the
    /// catalog as it loads.</summary>
    public bool OverridesShipped { get; internal set; }
    public required string Description { get; init; }
    /// <summary>The file's <c>size</c>, <c>[width, height]</c>.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>"top" (default) or "bottom".</summary>
    public string Anchor { get; init; } = "top";
    public IReadOnlyList<SourceDef> Sources { get; init; } = [];
    public IReadOnlyList<ComponentDef> Components { get; init; } = [];
    public IReadOnlyList<Knob> Knobs { get; init; } = [];
    /// <summary>A sentence shown when a requirement may be missing; null when there is none.</summary>
    public string? Requires { get; init; }

    /// <summary>Part ids never contain dots, so an override key's four-segment repeater-child form
    /// is unambiguous (plan D1).</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex PartId();

    /// <summary>Loads and validates a widget file. Throws <see cref="FormatException"/> naming the
    /// file and field on a bad file.</summary>
    public static WidgetTemplate Load(string path)
    {
        WidgetTemplateFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), WidgetJsonContext.Default.WidgetTemplateFile);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }
        if (file is null) throw new FormatException($"{path}: empty widget file");
        if (string.IsNullOrWhiteSpace(file.Name)) throw new FormatException($"{path}: missing \"name\"");
        if (string.IsNullOrWhiteSpace(file.Description)) throw new FormatException($"{path}: missing \"description\"");
        if (file.Size is null || file.Size.Length != 2) throw new FormatException($"{path}: \"size\" must be [width, height]");
        if (file.Size[0] <= 0 || file.Size[1] <= 0) throw new FormatException($"{path}: \"size\" must be positive");

        var anchor = (file.Anchor ?? "top").ToLowerInvariant();
        if (anchor is not ("top" or "bottom")) throw new FormatException($"{path}: \"anchor\" must be \"top\" or \"bottom\", was \"{file.Anchor}\"");

        var knobs = new List<Knob>();
        foreach (var k in file.Knobs ?? [])
        {
            if (string.IsNullOrWhiteSpace(k.Id)) throw new FormatException($"{path}: a knob is missing \"id\"");
            if (k.Type is null || !Enum.TryParse<KnobType>(k.Type, ignoreCase: true, out var kt))
                throw new FormatException($"{path}: knob \"{k.Id}\" has unknown \"type\" \"{k.Type}\"");
            knobs.Add(new Knob(k.Id, k.Label ?? k.Id, kt, k.Default ?? "", k.Sets ?? [], k.Choices, k.Min, k.Max));
        }
        if (knobs.Count > 5) throw new FormatException($"{path}: {knobs.Count} knobs exceeds the doctrine limit of 5");

        var components = file.Components ?? [];
        foreach (var c in components.Concat(components.OfType<RepeaterDef>().SelectMany(r => r.Template)))
            if (!PartId().IsMatch(c.Id))
                throw new FormatException($"{path}: part id \"{c.Id}\" must be letters, digits, '_' or '-', not starting with a digit");

        return new WidgetTemplate
        {
            Name = file.Name,
            Key = System.IO.Path.GetFileNameWithoutExtension(path),
            Path = path,
            Description = file.Description,
            Width = file.Size[0],
            Height = file.Size[1],
            Anchor = anchor,
            Sources = file.Sources ?? [],
            Components = components,
            Knobs = knobs,
            Requires = file.Requires,
        };
    }
}
