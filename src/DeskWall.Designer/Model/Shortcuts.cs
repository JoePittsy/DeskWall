namespace DeskWall.Designer.Model;

/// <summary>Every key the main window and the canvas answer to, as the shortcut sheet (F1 or ?)
/// lists them and the canvas menu shows them. Written by hand from <c>MainWindow.OnPreviewKeyDown</c>
/// and <c>PreviewView</c>'s key and pointer handling: a key added there is added here too.</summary>
public static class Shortcuts
{
    public sealed record Entry(string Keys, string What);

    public sealed record Group(string Name, IReadOnlyList<Entry> Entries);

    public const string MakeOrEdit = "Ctrl+Alt+K";
    public const string EditParts = "Enter";
    public const string Duplicate = "Ctrl+D";
    public const string Copy = "Ctrl+C";
    public const string Paste = "Ctrl+V";
    public const string Front = "Ctrl+]";
    public const string Back = "Ctrl+[";
    public const string Delete = "Delete";
    public const string ZoomToSelection = "Shift+2";
    public const string Sheet = "F1";

    public static readonly IReadOnlyList<Group> All =
    [
        new("Depth", [
            new(EditParts, "Go down a depth: a copy's parts, then its widget"),
            new("Esc", "Come back up a depth, then clear the selection"),
            new(MakeOrEdit, "Make a widget from loose parts, or edit the selected copy's widget"),
            new("Double-click", "Open a copy, then its widget; outside it, come back up"),
        ]),
        new("Selection", [
            new("Tab / Shift+Tab", "Next / previous thing on the canvas"),
            new("Ctrl+A", "Select everything at this depth"),
            new("Ctrl+click", "Add to or take from the selection"),
            new("Arrows", "Nudge 1 px (Ctrl: 10 px)"),
            new(Delete, "Remove what is selected"),
        ]),
        new("Edit", [
            new($"{Copy} / {Paste}", "Copy / paste"),
            new(Duplicate, "Duplicate"),
            new($"{Front} / {Back}", "Bring to front / send to back"),
            new("Ctrl+Z / Ctrl+Y", "Undo / redo"),
            new("Ctrl+S", "Apply: write the layout and paint the wallpaper"),
        ]),
        new("View", [
            new("Shift+1", "Fit the whole wallpaper"),
            new(ZoomToSelection, "Zoom to the selection"),
            new("Ctrl+plus / Ctrl+minus", "Zoom in / out"),
            new("Ctrl+0", "Zoom to 100%"),
            new("Ctrl+wheel", "Zoom about the pointer; the wheel pans (Shift: sideways)"),
        ]),
        new("While dragging", [
            new("Shift", "Snap to the grid"),
            new("Alt", "Let go of the smart guides"),
            new("Esc", "Cancel the drag"),
        ]),
        new("Help", [
            new("Menu key / Shift+F10", "The canvas menu"),
            new($"{Sheet} or ?", "Show or hide this list"),
        ]),
    ];
}
