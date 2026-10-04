using System.Text.Json.Serialization;

namespace DeskWall.Core.Layout;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextDef), "text")]
[JsonDerivedType(typeof(ImageDef), "image")]
[JsonDerivedType(typeof(BarDef), "bar")]
[JsonDerivedType(typeof(LineDef), "line")]
[JsonDerivedType(typeof(DialDef), "dial")]
[JsonDerivedType(typeof(ShortcutDef), "shortcut")]
[JsonDerivedType(typeof(RepeaterDef), "repeater")]
public abstract class ComponentDef
{
    public required string Id { get; set; }
    [JsonConverter(typeof(RectConverter))] public required Rect Rect { get; set; }
    public int Z { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PropertyValue? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PropertyValue? Y { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PropertyValue? W { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PropertyValue? H { get; set; }
    [JsonIgnore] public double GeometryScaleX { get; set; } = 1;
    [JsonIgnore] public double GeometryScaleY { get; set; } = 1;
    [JsonIgnore] public int GeometryOffsetX { get; set; }
    [JsonIgnore] public int GeometryOffsetY { get; set; }

    /// <summary>The widget instance id that owns this component ("&lt;templateKey&gt;-&lt;n&gt;"),
    /// or null for a component placed by hand. Ignored by resolve and render; the designer's
    /// widget model uses it to find, move and remove a widget's components as a unit
    /// (<c>docs/layout-format.md</c> "Copies").</summary>
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
    /// <summary>Blur radius for the effect, in pixels. "auto" (the default, and anything else that
    /// is not a number) means a tenth of the font size, floored at 1 px
    /// (<see cref="Render.TextStyle.DefaultRadius"/>): a flat 6 px is a drop shadow on a 64 px clock
    /// and a dark crust on a 13 px label. Same sentinel convention as
    /// <see cref="RepeaterDef.CellHeight"/>, and <c>LayoutScaler</c> already leaves a non-numeric
    /// literal alone when it scales a layout to another display.</summary>
    public PropertyValue EffectRadius { get; set; } = PropertyValue.Literal("auto");
    public PropertyValue EffectColor { get; set; } = PropertyValue.Literal("#A0000000");
}

public sealed class ImageDef : ComponentDef
{
    public required PropertyValue Source { get; set; }
    public PropertyValue Fit { get; set; } = PropertyValue.Literal("cover");
    public PropertyValue Radius { get; set; } = PropertyValue.Literal(0);
    public PropertyValue Opacity { get; set; } = PropertyValue.Literal(1);
    /// <summary>A colour every pixel is multiplied by (RGB by RGB, alpha by A); empty is none. A white
    /// PNG tinted by a Blend is one asset in any colour.</summary>
    public PropertyValue Tint { get; set; } = PropertyValue.Literal("");
}

public sealed class BarDef : ComponentDef
{
    public required PropertyValue Fraction { get; set; }
    public PropertyValue Track { get; set; } = PropertyValue.Literal("#46FFFFFF");
    public PropertyValue Fill { get; set; } = PropertyValue.Literal("#EBFFFFFF");
    public PropertyValue Threshold { get; set; } = PropertyValue.Literal(1);
    public PropertyValue ThresholdFill { get; set; } = PropertyValue.Literal("#D13438");
    public PropertyValue Direction { get; set; } = PropertyValue.Literal("horizontal");
    /// <summary>The halo's colour; empty is the fill's.</summary>
    public PropertyValue GlowColor { get; set; } = PropertyValue.Literal("");
    /// <summary>SVG path data (M L H V C Z) drawn instead of the box, its bounds stretched to Rect;
    /// empty is the plain box. The fill is the same path clipped to the fraction.</summary>
    public PropertyValue Shape { get; set; } = PropertyValue.Literal("");
    /// <summary>For a shape: 0 fills it, more strokes it this wide.</summary>
    public PropertyValue Thickness { get; set; } = PropertyValue.Literal(0);
    /// <summary>For a shape: a halo this many px round the lit part, in the fill colour. The path is
    /// inset by it so the halo stays inside Rect.</summary>
    public PropertyValue Glow { get; set; } = PropertyValue.Literal(0);
    /// <summary>Each of the four glow strokes' share of the glow colour's alpha. 0.12 is a halo you
    /// have to look for; 0.35 is one you cannot miss.</summary>
    public PropertyValue GlowStrength { get; set; } = PropertyValue.Literal(0.12);
    /// <summary>0..1, multiplied into track, fill and glow. Lets a second value (the weather)
    /// veil a bar whose colours are already bound to a first (the time of day).</summary>
    public PropertyValue Opacity { get; set; } = PropertyValue.Literal(1);
}

/// <summary>A thin arc showing one fraction. Same value semantics as <see cref="BarDef"/>; the
/// geometry is an arc centred in Rect instead of a filled box.</summary>
public sealed class DialDef : ComponentDef
{
    public PropertyValue Opacity { get; set; } = PropertyValue.Literal(1);
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

public sealed class LineDef : ComponentDef
{
    public required PropertyValue Values { get; set; }
    public PropertyValue Field { get; set; } = PropertyValue.Literal("v");
    public PropertyValue Stroke { get; set; } = PropertyValue.Literal("#AA9CCBEE");
    public PropertyValue Thickness { get; set; } = PropertyValue.Literal(2);
    public PropertyValue Glow { get; set; } = PropertyValue.Literal(0);
    /// <summary>The glow's colour; empty is the stroke's.</summary>
    public PropertyValue GlowColor { get; set; } = PropertyValue.Literal("");
    /// <summary>Same as the bar's: each glow stroke's share of the glow colour's alpha.</summary>
    public PropertyValue GlowStrength { get; set; } = PropertyValue.Literal(0.12);
    /// <summary>With a baseline, the colour of the area under the line; empty is the stroke at 12%.</summary>
    public PropertyValue AreaFill { get; set; } = PropertyValue.Literal("");
    public PropertyValue Min { get; set; } = PropertyValue.Literal(0);
    public PropertyValue Max { get; set; } = PropertyValue.Literal(1);
    public PropertyValue Baseline { get; set; } = PropertyValue.Literal("false");
}
