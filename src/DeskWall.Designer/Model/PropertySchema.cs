using System.Globalization;
using DeskWall.Core;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>What the properties panel shows for each ComponentDef type, in order, with editor kind.
/// The rows, order and accessors are Core's <see cref="ComponentProperties"/>; this adds only how
/// each is edited.</summary>
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

    /// <summary>The Core table (<see cref="ComponentProperties.For"/>) with each row's editor kind
    /// added, in the same order. Get and Set come from Core, so the designer and the expander can
    /// never disagree about what a property name reads or writes.</summary>
    public static IReadOnlyList<Prop> For(ComponentDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        lock (Tables)
        {
            if (!Tables.TryGetValue(def.GetType(), out var table))
                Tables[def.GetType()] = table = ComponentProperties.For(def)
                    .Select(p => Editors.TryGetValue(p.Name, out var e)
                        ? new Prop(p.Name, e.Editor, e.Choices, p.Get, p.Set)
                        : throw new NotSupportedException($"no editor for {def.GetType().Name}.{p.Name}"))
                    .ToArray();
            return table;
        }
    }

    private static readonly Dictionary<Type, Prop[]> Tables = new();

    /// <summary>Editor kind by property name. Where two types share a name (Fraction, Track, Fill,
    /// Threshold, ThresholdFill) they share an editor too.</summary>
    private static readonly Dictionary<string, (Editor Editor, string[]? Choices)> Editors = new()
    {
        ["Text"] = (Editor.Text, null),
        ["Font"] = (Editor.Font, null),
        ["Size"] = (Editor.Number, null),
        ["Weight"] = (Editor.Number, null),
        ["Color"] = (Editor.Color, null),
        ["Align"] = (Editor.Enum, AlignChoices),
        ["Effect"] = (Editor.Enum, EffectChoices),
        ["EffectRadius"] = (Editor.AutoNumber, null),
        ["EffectColor"] = (Editor.Color, null),
        ["Source"] = (Editor.Path, null),
        ["Fit"] = (Editor.Enum, FitChoices),
        ["Radius"] = (Editor.Number, null),
        ["Opacity"] = (Editor.Number, null),
        ["Fraction"] = (Editor.Number, null),
        ["Track"] = (Editor.Color, null),
        ["Fill"] = (Editor.Color, null),
        ["Threshold"] = (Editor.Number, null),
        ["ThresholdFill"] = (Editor.Color, null),
        ["Direction"] = (Editor.Enum, AxisChoices),
        ["Thickness"] = (Editor.Number, null),
        ["StartAngle"] = (Editor.Number, null),
        ["Sweep"] = (Editor.Number, null),
        ["Target"] = (Editor.Path, null),
        ["Tooltip"] = (Editor.Text, null),
        ["Slot"] = (Editor.Number, null),
        ["Items"] = (Editor.Binding, null),
        ["Axis"] = (Editor.Enum, AxisChoices),
        ["Gap"] = (Editor.Number, null),
        ["CellHeight"] = (Editor.AutoNumber, null),
    };

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
