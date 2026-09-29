using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>What the retired widget editor's document used to start from: an empty 172x40 draft,
/// and parts added a step apart. The designer itself makes drafts from a template
/// (<see cref="WidgetDocument.FromTemplate"/>); these keep the knob tests short.</summary>
internal static class Drafts
{
    public static WidgetDocument New()
        => WidgetDocument.FromTemplate(new WidgetTemplate { Name = "New widget", Description = "", Width = 172, Height = 40 });

    public static ComponentDef AddPart(this WidgetDocument doc, PartKind kind)
    {
        var def = WidgetDocument.NewPart(kind, 8 * doc.Model.Layout.Components.Count);
        doc.Model.Add(def);
        return def;
    }
}
