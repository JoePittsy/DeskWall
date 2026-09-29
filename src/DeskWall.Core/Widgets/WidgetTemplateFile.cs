using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Layout;

namespace DeskWall.Core.Widgets;

/// <summary>The JSON shape of <c>widgets/&lt;key&gt;.json</c>, exactly as written. Everything is
/// nullable so <see cref="WidgetTemplate.Load"/> can name the missing field itself rather than
/// surface a serializer message.</summary>
public sealed class WidgetTemplateFile
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

public sealed class KnobFile
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    /// <summary>Parsed with <c>Enum.TryParse&lt;KnobType&gt;</c> by the loader, so an unknown
    /// type gets a message naming the knob.</summary>
    public string? Type { get; set; }
    public string? Default { get; set; }
    public List<string>? Sets { get; set; }
    public List<string>? Choices { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
}

/// <summary>Source-generated, so widget files load under native AOT (plan D3). Same options as
/// <see cref="LayoutJsonContext"/>.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(WidgetTemplateFile))]
public partial class WidgetJsonContext : JsonSerializerContext;
