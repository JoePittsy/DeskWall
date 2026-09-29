namespace DeskWall.Designer.Model;

/// <summary>The three depths of the one canvas (brief section 3). Esc climbs one.</summary>
public enum DepthKind
{
    /// <summary>Select, move and resize placed copies and loose components.</summary>
    Layout,
    /// <summary>Edit one copy's parts in place; every change is an override on that copy.</summary>
    Copy,
    /// <summary>Edit the widget itself; every copy of its key follows.</summary>
    Widget,
}

/// <summary>Where the canvas is editing. Not part of the document and not undoable: an undo that
/// removes the copy or widget in view climbs out instead (<c>PruneDepth</c>, Task 2.3).</summary>
/// <param name="CopyId">The copy being edited at <see cref="DepthKind.Copy"/>, or the copy whose
/// origin a <see cref="DepthKind.Widget"/> edit is shown at (null for a widget with no copy in
/// this layout). Always null at <see cref="DepthKind.Layout"/>.</param>
/// <param name="WidgetKey">The widget key at <see cref="DepthKind.Copy"/> and
/// <see cref="DepthKind.Widget"/>; null at <see cref="DepthKind.Layout"/>.</param>
public sealed record Depth(DepthKind Kind, string? CopyId, string? WidgetKey)
{
    public static readonly Depth Layout = new(DepthKind.Layout, null, null);

    public static Depth Copy(string copyId, string widgetKey) => new(DepthKind.Copy, copyId, widgetKey);

    public static Depth Widget(string widgetKey, string? copyId) => new(DepthKind.Widget, copyId, widgetKey);
}
