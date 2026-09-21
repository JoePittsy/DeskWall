using System.Globalization;
using DeskWall.Core;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>What the properties panel shows for each ComponentDef type, in order, with editor kind.
/// Explicit table, no reflection (the Core defs are plain classes; reflection would also be a trim
/// hazard later).</summary>
public static class PropertySchema
{
    /// <summary><see cref="AutoNumber"/> is a pixel size the renderer may work out for itself; the
    /// rest are what they say.</summary>
    public enum Editor { Text, Number, AutoNumber, Color, Enum, Font, Path, Binding }

    /// <summary>The sentinel Core reads for "you decide": a text shadow's radius follows the font
    /// size, a repeater cell's height follows its first image's aspect ratio. It is a literal
    /// string in the JSON, not a number, which is why an ordinary numeric box shows the word
    /// <c>auto</c> back at the owner and an <see cref="Editor.AutoNumber"/> one does not.</summary>
    public const string Auto = "auto";

    public sealed record Prop(string Name, Editor Editor, string[]? Choices, Func<ComponentDef, PropertyValue?> Get, Action<ComponentDef, PropertyValue> Set);

    public static readonly string[] AlignChoices = ["Left", "Center", "Right"];
    public static readonly string[] EffectChoices = ["None", "Shadow", "Outline", "Plate"];
    public static readonly string[] FitChoices = ["Cover", "Contain", "Stretch"];
    public static readonly string[] AxisChoices = ["Horizontal", "Vertical"];

    public static IReadOnlyList<Prop> For(ComponentDef def) => def switch
    {
        TextDef => TextProps,
        ImageDef => ImageProps,
        BarDef => BarProps,
        DialDef => DialProps,
        ShortcutDef => ShortcutProps,
        RepeaterDef => RepeaterProps,
        _ => throw new NotSupportedException($"no property schema for {def.GetType().Name}"),
    };

    // ---- scalar <-> PropertyValue adapters, for the handful of fields that are not themselves
    // PropertyValue (Slot, Gap are plain int; Axis is a plain enum). A bound value cannot be stored
    // on these (there is nowhere on the model to keep the binding), so Set on a bound PropertyValue
    // is a no-op that keeps the field's current value.

    private static PropertyValue IntToProp(int v) => PropertyValue.Literal((double)v);

    private static int PropToInt(PropertyValue v, int fallback)
        => !v.IsBound && double.TryParse(v.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : fallback;

    private static PropertyValue AxisToProp(Axis a) => PropertyValue.Literal(a.ToString());

    private static Axis PropToAxis(PropertyValue v, Axis fallback)
        => !v.IsBound && Enum.TryParse<Axis>(v.LiteralText, ignoreCase: true, out var a) ? a : fallback;

    private static readonly Prop[] TextProps =
    [
        new("Text", Editor.Text, null, c => ((TextDef)c).Text, (c, v) => ((TextDef)c).Text = v),
        new("Font", Editor.Font, null, c => ((TextDef)c).Font, (c, v) => ((TextDef)c).Font = v),
        new("Size", Editor.Number, null, c => ((TextDef)c).Size, (c, v) => ((TextDef)c).Size = v),
        new("Weight", Editor.Number, null, c => ((TextDef)c).Weight, (c, v) => ((TextDef)c).Weight = v),
        new("Color", Editor.Color, null, c => ((TextDef)c).Color, (c, v) => ((TextDef)c).Color = v),
        new("Align", Editor.Enum, AlignChoices, c => ((TextDef)c).Align, (c, v) => ((TextDef)c).Align = v),
        new("Effect", Editor.Enum, EffectChoices, c => ((TextDef)c).Effect, (c, v) => ((TextDef)c).Effect = v),
        new("EffectRadius", Editor.AutoNumber, null, c => ((TextDef)c).EffectRadius, (c, v) => ((TextDef)c).EffectRadius = v),
        new("EffectColor", Editor.Color, null, c => ((TextDef)c).EffectColor, (c, v) => ((TextDef)c).EffectColor = v),
    ];

    private static readonly Prop[] ImageProps =
    [
        new("Source", Editor.Path, null, c => ((ImageDef)c).Source, (c, v) => ((ImageDef)c).Source = v),
        new("Fit", Editor.Enum, FitChoices, c => ((ImageDef)c).Fit, (c, v) => ((ImageDef)c).Fit = v),
        new("Radius", Editor.Number, null, c => ((ImageDef)c).Radius, (c, v) => ((ImageDef)c).Radius = v),
        new("Opacity", Editor.Number, null, c => ((ImageDef)c).Opacity, (c, v) => ((ImageDef)c).Opacity = v),
    ];

    private static readonly Prop[] BarProps =
    [
        new("Fraction", Editor.Number, null, c => ((BarDef)c).Fraction, (c, v) => ((BarDef)c).Fraction = v),
        new("Track", Editor.Color, null, c => ((BarDef)c).Track, (c, v) => ((BarDef)c).Track = v),
        new("Fill", Editor.Color, null, c => ((BarDef)c).Fill, (c, v) => ((BarDef)c).Fill = v),
        new("Threshold", Editor.Number, null, c => ((BarDef)c).Threshold, (c, v) => ((BarDef)c).Threshold = v),
        new("ThresholdFill", Editor.Color, null, c => ((BarDef)c).ThresholdFill, (c, v) => ((BarDef)c).ThresholdFill = v),
        new("Direction", Editor.Enum, AxisChoices, c => ((BarDef)c).Direction, (c, v) => ((BarDef)c).Direction = v),
    ];

    private static readonly Prop[] DialProps =
    [
        new("Fraction", Editor.Number, null, c => ((DialDef)c).Fraction, (c, v) => ((DialDef)c).Fraction = v),
        new("Track", Editor.Color, null, c => ((DialDef)c).Track, (c, v) => ((DialDef)c).Track = v),
        new("Fill", Editor.Color, null, c => ((DialDef)c).Fill, (c, v) => ((DialDef)c).Fill = v),
        new("Threshold", Editor.Number, null, c => ((DialDef)c).Threshold, (c, v) => ((DialDef)c).Threshold = v),
        new("ThresholdFill", Editor.Color, null, c => ((DialDef)c).ThresholdFill, (c, v) => ((DialDef)c).ThresholdFill = v),
        new("Thickness", Editor.Number, null, c => ((DialDef)c).Thickness, (c, v) => ((DialDef)c).Thickness = v),
        new("StartAngle", Editor.Number, null, c => ((DialDef)c).StartAngle, (c, v) => ((DialDef)c).StartAngle = v),
        new("Sweep", Editor.Number, null, c => ((DialDef)c).Sweep, (c, v) => ((DialDef)c).Sweep = v),
    ];

    private static readonly Prop[] ShortcutProps =
    [
        new("Target", Editor.Path, null, c => ((ShortcutDef)c).Target, (c, v) => ((ShortcutDef)c).Target = v),
        new("Tooltip", Editor.Text, null, c => ((ShortcutDef)c).Tooltip, (c, v) => ((ShortcutDef)c).Tooltip = v),
        new("Slot", Editor.Number, null, c => IntToProp(((ShortcutDef)c).Slot), (c, v) => ((ShortcutDef)c).Slot = PropToInt(v, ((ShortcutDef)c).Slot)),
    ];

    private static readonly Prop[] RepeaterProps =
    [
        new("Items", Editor.Binding, null, c => ((RepeaterDef)c).Items, (c, v) => ((RepeaterDef)c).Items = v),
        new("Axis", Editor.Enum, AxisChoices, c => AxisToProp(((RepeaterDef)c).Axis), (c, v) => ((RepeaterDef)c).Axis = PropToAxis(v, ((RepeaterDef)c).Axis)),
        new("Gap", Editor.Number, null, c => IntToProp(((RepeaterDef)c).Gap), (c, v) => ((RepeaterDef)c).Gap = PropToInt(v, ((RepeaterDef)c).Gap)),
        new("CellHeight", Editor.AutoNumber, null, c => ((RepeaterDef)c).CellHeight, (c, v) => ((RepeaterDef)c).CellHeight = v),
    ];

    // ---- "a number, or let the renderer decide" --------------------------------------------------

    /// <summary>What an <see cref="Editor.AutoNumber"/> box shows: the number, or nothing at all
    /// when the value is <see cref="Auto"/>. Nothing, rather than the word: an empty box with
    /// "auto" greyed behind it reads as a setting left alone, whereas the word typed into a
    /// numeric field reads as a value someone meant to be a number and got wrong.</summary>
    public static string AutoNumberText(PropertyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.IsBound || value.LiteralText is not { } text
            || string.Equals(text, Auto, StringComparison.OrdinalIgnoreCase)
            ? ""
            : text;
    }

    /// <summary>The literal to write back for what was typed into such a box: cleared (or the word
    /// itself) means <see cref="Auto"/>, a number means that number, and anything else is a typo
    /// that keeps <paramref name="current"/> - a numeric property is not a place to let "12px"
    /// through and find out at the next tick.</summary>
    public static string AutoNumberLiteral(string typed, string? current)
    {
        var text = (typed ?? "").Trim();
        if (text.Length == 0 || string.Equals(text, Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : current ?? Auto;
    }

    public static readonly IReadOnlyList<(string Name, Func<ComponentDef, int> Get, Action<ComponentDef, int> Set)> Geometry =
    [
        ("X", c => c.Rect.X, (c, v) => c.Rect = c.Rect with { X = v }),
        ("Y", c => c.Rect.Y, (c, v) => c.Rect = c.Rect with { Y = v }),
        ("W", c => c.Rect.W, (c, v) => c.Rect = c.Rect with { W = v }),
        ("H", c => c.Rect.H, (c, v) => c.Rect = c.Rect with { H = v }),
        ("Z", c => c.Z, (c, v) => c.Z = v),
    ];
}
