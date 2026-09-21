using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

public enum KnobType { Number, Text, Choice, Color, Drive, Town }

/// <summary>One control in a widget's knobs panel. See <c>docs/layout-format.md</c> "Widgets" for
/// the <see cref="Sets"/> grammar and the composite-value convention some knobs need.</summary>
public sealed record Knob(string Id, string Label, KnobType Type, string Default, IReadOnlyList<string> Sets, IReadOnlyList<string>? Choices, double? Min, double? Max);

/// <summary>A widget recipe loaded from <c>widgets/&lt;key&gt;.json</c>: some sources, some
/// components in template-local coordinates (the widget's own top-left is (0, 0)), and up to
/// five knobs. Never mutated once loaded; <see cref="WidgetInstance"/> copies from it.</summary>
public sealed class WidgetTemplate
{
    public required string Name { get; init; }
    /// <summary>The file name without ".json" -- not a field in the file.</summary>
    public string Key { get; init; } = "";
    public required string Description { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>"top" (default) or "bottom" -- which end of the column <see cref="Arranger"/> stacks
    /// this widget from.</summary>
    public string Anchor { get; init; } = "top";
    public IReadOnlyList<SourceDef> Sources { get; init; } = [];
    public IReadOnlyList<ComponentDef> Components { get; init; } = [];
    public IReadOnlyList<Knob> Knobs { get; init; } = [];
    /// <summary>A sentence shown on the gallery card when a requirement (an NVIDIA GPU, Tailscale,
    /// Steam secrets) may be missing; null when the widget has none.</summary>
    public string? Requires { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static WidgetTemplate Load(string path)
    {
        WidgetTemplateFile? file;
        try
        {
            file = JsonSerializer.Deserialize<WidgetTemplateFile>(File.ReadAllText(path), JsonOptions);
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

        return new WidgetTemplate
        {
            Name = file.Name,
            Key = Path.GetFileNameWithoutExtension(path),
            Description = file.Description,
            Width = file.Size[0],
            Height = file.Size[1],
            Anchor = anchor,
            Sources = file.Sources ?? [],
            Components = file.Components ?? [],
            Knobs = knobs,
            Requires = file.Requires,
        };
    }

    /// <summary>A layout containing just this widget at (0, 0) on <paramref name="baseImage"/>, for
    /// a gallery card render. The template's own components/sources are copied, never shared, so
    /// the caller can freely hand the result to a renderer without risking the template.</summary>
    public LayoutFile Preview(string baseImage) => new()
    {
        BaseImage = baseImage,
        Sources = Sources.Select(WidgetJson.CloneSource).ToList(),
        Components = WidgetJson.CloneComponents(Components),
    };
}

// ---- JSON shape of widgets/<key>.json; deserialized by reflection (the designer is JIT, not
// AOT, so there is no source-generated context here) and mapped into WidgetTemplate above, which
// also lets Load produce error messages that name the file and the field. ----

internal sealed class WidgetTemplateFile
{
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int[]? Size { get; set; }
    public string? Anchor { get; set; }
    public string? Requires { get; set; }
    public List<SourceDef>? Sources { get; set; }
    public List<ComponentDef>? Components { get; set; }
    public List<KnobFile>? Knobs { get; set; }
}

internal sealed class KnobFile
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    public string? Type { get; set; }
    public string? Default { get; set; }
    public List<string>? Sets { get; set; }
    public List<string>? Choices { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
}
