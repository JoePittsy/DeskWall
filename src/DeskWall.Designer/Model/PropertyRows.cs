using System.Globalization;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>How the properties panel presents <see cref="PropertySchema"/>: each property's group,
/// its human label, and whether it is shown as a percentage. The schema says what a property is and
/// how it is edited; this says where it sits and what it is called. One table by property name, so
/// a name two types share (Fraction, Track, Fill, Threshold, ThresholdFill) reads the same on both.
///
/// The mapping, by group:
/// <list type="bullet">
/// <item>Content (what it shows or does): Text, Source, Fraction, Items, Target, Tooltip, Slot.</item>
/// <item>Type: Font, Size, Weight, Align, Effect, EffectRadius.</item>
/// <item>Colour: Color, EffectColor, Track, Fill, Threshold, ThresholdFill, Opacity. Threshold sits
/// here, beside the colour it switches to, because what it changes is a colour.</item>
/// <item>Arc (dial only): Thickness, StartAngle, Sweep.</item>
/// <item>Geometry (shape and arrangement): Fit, Radius, Direction, Axis, Gap, CellHeight. The rect
/// and z editors (<see cref="PropertySchema.Geometry"/>) belong at the head of this group;
/// <see cref="Label"/> names them too.</item>
/// </list></summary>
public static class PropertyRows
{
    /// <summary>In display order.</summary>
    public enum Group { Content, Type, Colour, Arc, Geometry }

    /// <summary>One row of the panel. <see cref="Percent"/> rows store a fraction (0.9) and show a
    /// percentage (90): go through <see cref="PercentText"/> and <see cref="FromPercentText"/>.</summary>
    public sealed record Row(PropertySchema.Prop Prop, Group Group, string Label, bool Percent)
    {
        public string Name => Prop.Name;
    }

    /// <summary>Every schema property of <paramref name="def"/>, exactly once, ordered by group and
    /// then by schema order within the group.</summary>
    public static IReadOnlyList<Row> For(ComponentDef def)
        => PropertySchema.For(def)
            .Select(p => Rows.TryGetValue(p.Name, out var r)
                ? new Row(p, r.Group, r.Label, r.Percent)
                : throw new NotSupportedException($"no row for {def.GetType().Name}.{p.Name}"))
            .OrderBy(r => r.Group) // OrderBy is stable, so schema order survives inside a group
            .ToArray();

    /// <summary>The human label for a schema or geometry property name; the name itself if unknown.</summary>
    public static string Label(string name)
        => Rows.TryGetValue(name, out var r) ? r.Label : GeometryLabels.GetValueOrDefault(name, name);

    private static readonly Dictionary<string, string> GeometryLabels = new()
    {
        ["X"] = "X", ["Y"] = "Y", ["W"] = "Width", ["H"] = "Height", ["Z"] = "Stacking order",
    };

    private static readonly Dictionary<string, (Group Group, string Label, bool Percent)> Rows = new()
    {
        ["Text"] = (Group.Content, "Text", false),
        ["Source"] = (Group.Content, "Image", false),
        ["Fraction"] = (Group.Content, "Value", true),
        ["Items"] = (Group.Content, "Items", false),
        ["Target"] = (Group.Content, "Opens", false),
        ["Tooltip"] = (Group.Content, "Tooltip", false),
        ["Slot"] = (Group.Content, "Desktop slot", false),

        ["Font"] = (Group.Type, "Font", false),
        ["Size"] = (Group.Type, "Size", false),
        ["Weight"] = (Group.Type, "Weight", false),
        ["Align"] = (Group.Type, "Alignment", false),
        ["Effect"] = (Group.Type, "Effect", false),
        ["EffectRadius"] = (Group.Type, "Effect size", false),

        ["Color"] = (Group.Colour, "Colour", false),
        ["EffectColor"] = (Group.Colour, "Effect colour", false),
        ["Track"] = (Group.Colour, "Track", false),
        ["Fill"] = (Group.Colour, "Fill", false),
        ["Threshold"] = (Group.Colour, "Warn at", true),
        ["ThresholdFill"] = (Group.Colour, "Warn colour", false),
        ["Opacity"] = (Group.Colour, "Opacity", true),

        ["Thickness"] = (Group.Arc, "Stroke width", false),
        ["StartAngle"] = (Group.Arc, "Start angle", false),
        ["Sweep"] = (Group.Arc, "Sweep", false),

        ["Fit"] = (Group.Geometry, "Fit", false),
        ["Radius"] = (Group.Geometry, "Corner radius", false),
        ["Direction"] = (Group.Geometry, "Direction", false),
        ["Axis"] = (Group.Geometry, "Direction", false),
        ["Gap"] = (Group.Geometry, "Gap", false),
        ["CellHeight"] = (Group.Geometry, "Cell height", false),
    };

    /// <summary>A number knob that only sets percentage properties ("Warn at" sets a dial's
    /// threshold): shown and typed as a percentage, like the property row it stands for.</summary>
    public static bool IsPercentKnob(DeskWall.Core.Widgets.Knob knob)
    {
        ArgumentNullException.ThrowIfNull(knob);
        return knob.Type == DeskWall.Core.Widgets.KnobType.Number && knob.Sets.Count > 0 && knob.Sets.All(set =>
        {
            var parts = set.Split('.');
            return parts.Length >= 3 && parts[0] == "components" && !set.Contains('=', StringComparison.Ordinal)
                && Rows.FirstOrDefault(r => string.Equals(r.Key, parts[^1], StringComparison.OrdinalIgnoreCase)).Value.Percent;
        });
    }

    // ---- percentages: 0.9 in the file, 90 on screen ----------------------------------------------
    // Decimal, not double: 0.123 * 100 is 12.299999999999999 in double, and 12.3 / 100 is not the
    // double nearest 0.123. Through decimal both directions are exact to the 0.1% the box shows, so
    // opening a row and leaving it never rewrites the file.

    /// <summary>A fraction literal as the percentage to show, to 0.1% ("90", "12.5"); null for a
    /// binding or a literal that is not a number, which the row shows as it is.</summary>
    public static string? PercentText(PropertyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsBound || !double.TryParse(value.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
            || !double.IsFinite(fraction) || Math.Abs(fraction) > 1e12) return null;
        return Math.Round((decimal)fraction * 100m, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>What was typed into a percentage box ("90", "90%", " 12.5 % ") as the fraction literal
    /// to store; null for anything that is not a number, which should keep the current value.</summary>
    public static PropertyValue? FromPercentText(string? typed)
    {
        var text = (typed ?? "").Trim().TrimEnd('%').TrimEnd();
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return null;
        return PropertyValue.Literal((double)(Math.Round(percent, 1, MidpointRounding.AwayFromZero) / 100m));
    }
}
