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
public sealed class WidgetTemplate
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

    /// <summary>Loads and validates a widget file. Throws <see cref="FormatException"/> naming the
    /// file and field on a bad file.</summary>
    public static WidgetTemplate Load(string path) => throw new NotImplementedException("Task 1.2");
}
