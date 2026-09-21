using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>Deep-copy helpers shared by <see cref="WidgetTemplate"/> and <see cref="WidgetInstance"/>.
/// Components clone through a JSON round trip (same trick <c>DesignerModel.Clone</c> uses) so
/// every subclass-specific field -- and a repeater's own template list -- copies correctly without
/// hand-written cases per <c>ComponentDef</c> subtype.</summary>
internal static class WidgetJson
{
    public static List<ComponentDef> CloneComponents(IReadOnlyList<ComponentDef> defs)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = defs.ToList() }.ToJson()).Components;

    public static SourceDef CloneSource(SourceDef s) => new()
    {
        Name = s.Name,
        Type = s.Type,
        EverySeconds = s.EverySeconds,
        Settings = new Dictionary<string, string>(s.Settings),
    };
}
