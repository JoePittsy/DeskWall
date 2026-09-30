using System.Globalization;

namespace DeskWall.Core.Layout;

/// <summary>Every editable property of each <see cref="ComponentDef"/> type, as an explicit
/// (name, get, set) table: no reflection, so it is AOT-safe (plan D3). <see cref="For"/> has the
/// same names in the same order as the designer's <c>Model/PropertySchema.cs</c>, which takes its
/// Get/Set from here from Task 2.1. <see cref="Find"/> also answers the geometry pseudo-properties
/// <c>rect</c> (literal <c>"x,y,w,h"</c>) and <c>z</c>; <c>hidden</c> (<see cref="Hidden"/>) is not a
/// property of the part at all and is handled by <c>Widgets.KnobSets</c>.</summary>
public static class ComponentProperties
{
    public sealed record Prop(string Name, Func<ComponentDef, PropertyValue> Get, Action<ComponentDef, PropertyValue> Set);

    public const string Hidden = "hidden";
    private static readonly Prop[] GeometryProps =
    [
        Geometry("X", c => c.X, (c, v) => c.X = v, r => r.X, (r, n) => r with { X = n }),
        Geometry("Y", c => c.Y, (c, v) => c.Y = v, r => r.Y, (r, n) => r with { Y = n }),
        Geometry("W", c => c.W, (c, v) => c.W = v, r => r.W, (r, n) => r with { W = n }),
        Geometry("H", c => c.H, (c, v) => c.H = v, r => r.H, (r, n) => r with { H = n }),
    ];

    /// <summary>x/y/w/h read through to the rect unless set. A plain number is written back into the
    /// rect, so only a binding (or an unparseable literal) is ever held separately.</summary>
    private static Prop Geometry(string name, Func<ComponentDef, PropertyValue?> field, Action<ComponentDef, PropertyValue?> setField,
        Func<Rect, int> read, Func<Rect, int, Rect> write)
        => new(name, c => field(c) ?? PropertyValue.Literal(read(c.Rect)), (c, v) =>
        {
            if (!v.IsBound && double.TryParse(v.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n))
            {
                c.Rect = write(c.Rect, (int)Math.Clamp(Math.Round(n), -1000000, 1000000));
                setField(c, null);
            }
            else setField(c, v);
        });

    /// <summary>Whether x/y/w/h (by name, any case) is set on the part rather than read from its rect.
    /// False for every other property name.</summary>
    public static bool IsHeldGeometry(ComponentDef c, string name) => name.ToUpperInvariant() switch
    {
        "X" => c.X is not null,
        "Y" => c.Y is not null,
        "W" => c.W is not null,
        "H" => c.H is not null,
        _ => false,
    };

    public static bool IsGeometry(string name) => name.ToUpperInvariant() is "X" or "Y" or "W" or "H";

    public static IReadOnlyList<Prop> For(ComponentDef def) => [.. Specific(def), .. GeometryProps];
    private static IReadOnlyList<Prop> Specific(ComponentDef def) => def switch
    {
        TextDef => TextProps,
        ImageDef => ImageProps,
        BarDef => BarProps,
        LineDef => LineProps,
        DialDef => DialProps,
        ShortcutDef => ShortcutProps,
        RepeaterDef => RepeaterProps,
        _ => throw new NotSupportedException($"no property table for {def.GetType().Name}"),
    };

    /// <summary><see cref="For"/> plus <see cref="RectProp"/> and <see cref="ZProp"/>: everything an
    /// override can set on a part.</summary>
    public static IEnumerable<Prop> All(ComponentDef def) => [.. For(def), RectProp, ZProp];

    /// <summary>By name, ignoring case (override keys are camelCase, the table is PascalCase).</summary>
    public static Prop? Find(ComponentDef def, string name)
        => All(def).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A literal "x,y,w,h". A value that does not parse (or a binding) leaves the rect alone.</summary>
    public static readonly Prop RectProp = new("Rect",
        c => PropertyValue.Literal(FormatRect(c.Rect)),
        (c, v) => { if (TryParseRect(v, out var r)) c.Rect = r; });

    public static readonly Prop ZProp = new("Z", c => IntToProp(c.Z), (c, v) => c.Z = PropToInt(v, c.Z));

    public static string FormatRect(Rect r) => string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y},{r.W},{r.H}");

    public static bool TryParseRect(PropertyValue v, out Rect rect)
    {
        rect = default;
        var parts = v.IsBound ? [] : (v.LiteralText ?? "").Split(',');
        if (parts.Length != 4) return false;
        var n = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
        rect = new Rect(n[0], n[1], n[2], n[3]);
        return true;
    }

    // ---- adapters for the fields that are not PropertyValue (Slot, Gap, Z: int; Axis: enum). A
    // bound value has nowhere to live on these, so setting one keeps the current value.

    private static PropertyValue IntToProp(int v) => PropertyValue.Literal((double)v);

    private static int PropToInt(PropertyValue v, int fallback)
        => !v.IsBound && double.TryParse(v.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : fallback;

    private static PropertyValue AxisToProp(Axis a) => PropertyValue.Literal(a.ToString());

    private static Axis PropToAxis(PropertyValue v, Axis fallback)
        => !v.IsBound && Enum.TryParse<Axis>(v.LiteralText, ignoreCase: true, out var a) ? a : fallback;

    private static readonly Prop[] TextProps =
    [
        new("Text", c => ((TextDef)c).Text, (c, v) => ((TextDef)c).Text = v),
        new("Font", c => ((TextDef)c).Font, (c, v) => ((TextDef)c).Font = v),
        new("Size", c => ((TextDef)c).Size, (c, v) => ((TextDef)c).Size = v),
        new("Weight", c => ((TextDef)c).Weight, (c, v) => ((TextDef)c).Weight = v),
        new("Color", c => ((TextDef)c).Color, (c, v) => ((TextDef)c).Color = v),
        new("Align", c => ((TextDef)c).Align, (c, v) => ((TextDef)c).Align = v),
        new("Effect", c => ((TextDef)c).Effect, (c, v) => ((TextDef)c).Effect = v),
        new("EffectRadius", c => ((TextDef)c).EffectRadius, (c, v) => ((TextDef)c).EffectRadius = v),
        new("EffectColor", c => ((TextDef)c).EffectColor, (c, v) => ((TextDef)c).EffectColor = v),
    ];

    private static readonly Prop[] ImageProps =
    [
        new("Source", c => ((ImageDef)c).Source, (c, v) => ((ImageDef)c).Source = v),
        new("Fit", c => ((ImageDef)c).Fit, (c, v) => ((ImageDef)c).Fit = v),
        new("Radius", c => ((ImageDef)c).Radius, (c, v) => ((ImageDef)c).Radius = v),
        new("Opacity", c => ((ImageDef)c).Opacity, (c, v) => ((ImageDef)c).Opacity = v),
        new("Tint", c => ((ImageDef)c).Tint, (c, v) => ((ImageDef)c).Tint = v),
    ];

    private static readonly Prop[] LineProps =
    [
        new("Values", c => ((LineDef)c).Values, (c, v) => ((LineDef)c).Values = v),
        new("Field", c => ((LineDef)c).Field, (c, v) => ((LineDef)c).Field = v),
        new("Stroke", c => ((LineDef)c).Stroke, (c, v) => ((LineDef)c).Stroke = v),
        new("Thickness", c => ((LineDef)c).Thickness, (c, v) => ((LineDef)c).Thickness = v),
        new("Glow", c => ((LineDef)c).Glow, (c, v) => ((LineDef)c).Glow = v),
        new("GlowColor", c => ((LineDef)c).GlowColor, (c, v) => ((LineDef)c).GlowColor = v),
        new("GlowStrength", c => ((LineDef)c).GlowStrength, (c, v) => ((LineDef)c).GlowStrength = v),
        new("AreaFill", c => ((LineDef)c).AreaFill, (c, v) => ((LineDef)c).AreaFill = v),
        new("Min", c => ((LineDef)c).Min, (c, v) => ((LineDef)c).Min = v),
        new("Max", c => ((LineDef)c).Max, (c, v) => ((LineDef)c).Max = v),
        new("Baseline", c => ((LineDef)c).Baseline, (c, v) => ((LineDef)c).Baseline = v),
    ];

    private static readonly Prop[] BarProps =
    [
        new("Fraction", c => ((BarDef)c).Fraction, (c, v) => ((BarDef)c).Fraction = v),
        new("Track", c => ((BarDef)c).Track, (c, v) => ((BarDef)c).Track = v),
        new("Fill", c => ((BarDef)c).Fill, (c, v) => ((BarDef)c).Fill = v),
        new("Threshold", c => ((BarDef)c).Threshold, (c, v) => ((BarDef)c).Threshold = v),
        new("ThresholdFill", c => ((BarDef)c).ThresholdFill, (c, v) => ((BarDef)c).ThresholdFill = v),
        new("Direction", c => ((BarDef)c).Direction, (c, v) => ((BarDef)c).Direction = v),
        new("GlowColor", c => ((BarDef)c).GlowColor, (c, v) => ((BarDef)c).GlowColor = v),
        new("Shape", c => ((BarDef)c).Shape, (c, v) => ((BarDef)c).Shape = v),
        new("Thickness", c => ((BarDef)c).Thickness, (c, v) => ((BarDef)c).Thickness = v),
        new("Glow", c => ((BarDef)c).Glow, (c, v) => ((BarDef)c).Glow = v),
        new("GlowStrength", c => ((BarDef)c).GlowStrength, (c, v) => ((BarDef)c).GlowStrength = v),
        new("Opacity", c => ((BarDef)c).Opacity, (c, v) => ((BarDef)c).Opacity = v),
    ];

    private static readonly Prop[] DialProps =
    [
        new("Opacity", c => ((DialDef)c).Opacity, (c, v) => ((DialDef)c).Opacity = v),
        new("Fraction", c => ((DialDef)c).Fraction, (c, v) => ((DialDef)c).Fraction = v),
        new("Track", c => ((DialDef)c).Track, (c, v) => ((DialDef)c).Track = v),
        new("Fill", c => ((DialDef)c).Fill, (c, v) => ((DialDef)c).Fill = v),
        new("Threshold", c => ((DialDef)c).Threshold, (c, v) => ((DialDef)c).Threshold = v),
        new("ThresholdFill", c => ((DialDef)c).ThresholdFill, (c, v) => ((DialDef)c).ThresholdFill = v),
        new("Thickness", c => ((DialDef)c).Thickness, (c, v) => ((DialDef)c).Thickness = v),
        new("StartAngle", c => ((DialDef)c).StartAngle, (c, v) => ((DialDef)c).StartAngle = v),
        new("Sweep", c => ((DialDef)c).Sweep, (c, v) => ((DialDef)c).Sweep = v),
    ];

    private static readonly Prop[] ShortcutProps =
    [
        new("Target", c => ((ShortcutDef)c).Target, (c, v) => ((ShortcutDef)c).Target = v),
        new("Tooltip", c => ((ShortcutDef)c).Tooltip, (c, v) => ((ShortcutDef)c).Tooltip = v),
        new("Slot", c => IntToProp(((ShortcutDef)c).Slot), (c, v) => ((ShortcutDef)c).Slot = PropToInt(v, ((ShortcutDef)c).Slot)),
    ];

    private static readonly Prop[] RepeaterProps =
    [
        new("Items", c => ((RepeaterDef)c).Items, (c, v) => ((RepeaterDef)c).Items = v),
        new("Axis", c => AxisToProp(((RepeaterDef)c).Axis), (c, v) => ((RepeaterDef)c).Axis = PropToAxis(v, ((RepeaterDef)c).Axis)),
        new("Gap", c => IntToProp(((RepeaterDef)c).Gap), (c, v) => ((RepeaterDef)c).Gap = PropToInt(v, ((RepeaterDef)c).Gap)),
        new("CellHeight", c => ((RepeaterDef)c).CellHeight, (c, v) => ((RepeaterDef)c).CellHeight = v),
    ];
}
