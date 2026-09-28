namespace DeskWall.Core.Layout;

/// <summary>What a layout remembers about one widget instance, so the designer's widget picker
/// can re-edit its knobs or re-create it after a template update. Lives in Core (not the
/// designer's <c>Model/Widgets/</c> namespace, where the rest of the widget model lives) purely
/// so <see cref="LayoutJsonContext"/>'s source generator can carry it as part of
/// <see cref="LayoutFile.Widgets"/>; see <c>docs/layout-format.md</c> "Widgets". Ignored by
/// resolve and render, same as <see cref="ComponentDef.Widget"/>.</summary>
public sealed class WidgetRecord
{
    /// <summary>The widget template's key (its file name without ".json").</summary>
    public required string Template { get; set; }

    /// <summary>Knob id to the raw value last written through it (see "Widgets" for the
    /// composite-value convention some knob types use), so a knob can be shown back and
    /// re-applied without recomputing anything.</summary>
    public Dictionary<string, string> Knobs { get; set; } = new();

    /// <summary>Retained for backwards compatibility and ignored by the designer. It used to
    /// exempt an instance from the designer's fixed right-hand column (<c>Arranger.Arrange</c>,
    /// which still honours it and still generates the starter layouts). The designer's canvas has
    /// no column any more - every widget is free-placed - so nothing sets or reads this any more;
    /// it stays a property only so a layout written with <c>"unlocked": true/false</c> in it still
    /// parses.</summary>
    public bool Unlocked { get; set; }
}
