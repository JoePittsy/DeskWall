using System.Text.Json.Serialization;

namespace DeskWall.Core.Layout;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextDef), "text")]
[JsonDerivedType(typeof(ImageDef), "image")]
[JsonDerivedType(typeof(BarDef), "bar")]
[JsonDerivedType(typeof(DialDef), "dial")]
[JsonDerivedType(typeof(ShortcutDef), "shortcut")]
[JsonDerivedType(typeof(RepeaterDef), "repeater")]
public abstract class ComponentDef
{
    public required string Id { get; set; }
    [JsonConverter(typeof(RectConverter))] public required Rect Rect { get; set; }
    public int Z { get; set; }

    /// <summary>The designer's widget instance this component was stamped from, or null for a
    /// component placed by hand. Resolve and render ignore it entirely; it exists so the designer
    /// can select, move and remove a widget as one thing. Written only when set, so a layout
    /// authored without the designer stays free of it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Widget { get; set; }
}

public sealed class TextDef : ComponentDef
{
    public required PropertyValue Text { get; set; }
    public PropertyValue Font { get; set; } = PropertyValue.Literal("Segoe UI");
    public PropertyValue Size { get; set; } = PropertyValue.Literal(16);
    public PropertyValue Weight { get; set; } = PropertyValue.Literal(400);
    public PropertyValue Color { get; set; } = PropertyValue.Literal("#EBFFFFFF");
    public PropertyValue Align { get; set; } = PropertyValue.Literal("left");
    public PropertyValue Effect { get; set; } = PropertyValue.Literal("shadow");
    public PropertyValue EffectRadius { get; set; } = PropertyValue.Literal(6);
    public PropertyValue EffectColor { get; set; } = PropertyValue.Literal("#A0000000");
}

public sealed class ImageDef : ComponentDef
{
    public required PropertyValue Source { get; set; }
    public PropertyValue Fit { get; set; } = PropertyValue.Literal("cover");
    public PropertyValue Radius { get; set; } = PropertyValue.Literal(0);
    public PropertyValue Opacity { get; set; } = PropertyValue.Literal(1);
}

public sealed class BarDef : ComponentDef
{
    public required PropertyValue Fraction { get; set; }
    public PropertyValue Track { get; set; } = PropertyValue.Literal("#46FFFFFF");
    public PropertyValue Fill { get; set; } = PropertyValue.Literal("#EBFFFFFF");
    public PropertyValue Threshold { get; set; } = PropertyValue.Literal(1);
    public PropertyValue ThresholdFill { get; set; } = PropertyValue.Literal("#D13438");
    public PropertyValue Direction { get; set; } = PropertyValue.Literal("horizontal");
}

/// <summary>A thin arc showing one fraction. Same value semantics as <see cref="BarDef"/>; the
/// geometry is an arc centred in Rect instead of a filled box.</summary>
public sealed class DialDef : ComponentDef
{
    public required PropertyValue Fraction { get; set; }
    public PropertyValue Track { get; set; } = PropertyValue.Literal("#46FFFFFF");
    public PropertyValue Fill { get; set; } = PropertyValue.Literal("#EBFFFFFF");
    public PropertyValue Threshold { get; set; } = PropertyValue.Literal(1);
    public PropertyValue ThresholdFill { get; set; } = PropertyValue.Literal("#D13438");
    public PropertyValue Thickness { get; set; } = PropertyValue.Literal(6);
    /// <summary>Degrees clockwise from 12 o'clock where the sweep begins.</summary>
    public PropertyValue StartAngle { get; set; } = PropertyValue.Literal(225);
    /// <summary>Degrees of arc for fraction 1.</summary>
    public PropertyValue Sweep { get; set; } = PropertyValue.Literal(270);
}

public sealed class ShortcutDef : ComponentDef
{
    public required PropertyValue Target { get; set; }
    public PropertyValue Tooltip { get; set; } = PropertyValue.Literal("");
    /// <summary>Base slot; repeater children add their index.</summary>
    public int Slot { get; set; }
}

public sealed class RepeaterDef : ComponentDef
{
    public required PropertyValue Items { get; set; }
    public Axis Axis { get; set; } = Axis.Vertical;
    public int Gap { get; set; }
    /// <summary>Pixels, or "auto" to take the height from the first image child's aspect ratio.</summary>
    public PropertyValue CellHeight { get; set; } = PropertyValue.Literal("auto");
    public required List<ComponentDef> Template { get; set; }
}
