namespace DeskWall.Designer.Model;

/// <summary>One way to show a value as text: what the Data panel's popover lists, and the
/// <c>| "..."</c> half of the binding it writes. <see cref="Format"/> is what Core's
/// <c>Value.ToText</c> takes; null means the value as it is.</summary>
/// <param name="Label">How the preset reads in a list, shown on a representative value.</param>
public sealed record FormatPreset(string Label, string? Format);

/// <summary>The presets per <see cref="ValueKind"/>, the first being the default. Invariant culture,
/// as the renderer formats: <c>{0:0%}</c> rather than <c>P0</c>, whose invariant pattern puts a space
/// before the sign ("27 %").</summary>
public static class FormatPresets
{
    public static IReadOnlyList<FormatPreset> For(ValueKind kind) => kind switch
    {
        ValueKind.Fraction => Fraction,
        ValueKind.Number => Number,
        ValueKind.Timestamp => Timestamp,
        ValueKind.Bool => Bool,
        ValueKind.Text => Text,
        _ => [],   // a list has no text form worth drawing ("[3 items]"); it feeds a repeater
    };

    /// <summary>The time a timestamp dropped on a text part gets, and a Clock's format.</summary>
    public const string Time = "HH:mm";

    private static readonly FormatPreset[] Fraction =
    [
        new("27%", "{0:0%}"),
        new("27.4%", "{0:0.0%}"),
        new("0.27", "{0:0.00}"),
    ];

    private static readonly FormatPreset[] Number =
    [
        new("64", "{0:0}"),
        new("64.4", "{0:0.0}"),
        new("64%", "{0:0}%"),
        new("64 GB free", "{0:0} GB free"),
    ];

    private static readonly FormatPreset[] Timestamp =
    [
        new("14:05", Time),
        new("2:05 PM", "h:mm tt"),
        new("Tue 29 Sep", "ddd d MMM"),
        new("29 Sep 2026", "d MMM yyyy"),
        new("Tuesday", "dddd"),
    ];

    private static readonly FormatPreset[] Bool =
    [
        new("Yes / No", "?true=Yes,false=No"),
        new("On / Off", "?true=On,false=Off"),
        new("Only when true", "?true=Yes"),
    ];

    private static readonly FormatPreset[] Text = [new("As it is", null)];
}
