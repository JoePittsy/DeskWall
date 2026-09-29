namespace DeskWall.Core.Layout;

/// <summary>One placed, linked copy of a widget in a v2 layout (<c>docs/layout-format.md</c>
/// "Copies"). <see cref="Widgets.WidgetExpander"/> turns it into ordinary components before
/// resolve; resolve and render never see a copy.</summary>
public sealed class WidgetCopy
{
    /// <summary>Unique among the layout's copies ("&lt;widgetKey&gt;-&lt;n&gt;"). Expanded part ids
    /// are "&lt;Id&gt;.&lt;partId&gt;".</summary>
    public required string Id { get; set; }

    /// <summary>The widget's key: its file name without ".json". Never changes after creation.</summary>
    public required string Widget { get; set; }

    /// <summary>The copy's origin on the canvas; every part's rect is offset by it.</summary>
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Added to every part's own z. 0 keeps the widget's z exactly as authored.</summary>
    public int Z { get; set; }

    /// <summary>Knob id to the value the owner set. A knob at its default is not stored, so a
    /// changed default reaches every copy that never touched it.</summary>
    public Dictionary<string, string> Knobs { get; set; } = new();

    /// <summary>Override key (the knob <c>sets</c> grammar, plus the part pseudo-properties
    /// <c>rect</c>, <c>z</c> and <c>hidden</c>) to its value. Applied after knobs.</summary>
    public Dictionary<string, PropertyValue> Overrides { get; set; } = new();
}
