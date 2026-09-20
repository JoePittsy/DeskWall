using DeskWall.Core.Layout;

namespace DeskWall.Designer.Views;

/// <summary>DesignerModel.Find only sees top-level components; a repeater's Template children have
/// their own ids but are not part of Layout.Components. This walks both, for LayersPanel and
/// PropertiesPanel, without changing the seam.</summary>
internal static class ComponentLookup
{
    public readonly record struct Found(ComponentDef Def, RepeaterDef? Parent);

    public static Found? Find(LayoutFile layout, string id)
    {
        foreach (var c in layout.Components)
        {
            if (c.Id == id) return new Found(c, null);
            if (c is RepeaterDef r)
                foreach (var t in r.Template)
                    if (t.Id == id) return new Found(t, r);
        }
        return null;
    }

    /// <summary>Top level first, each repeater immediately followed by its own template children.</summary>
    public static IEnumerable<Found> AllIncludingTemplates(LayoutFile layout)
    {
        foreach (var c in layout.Components)
        {
            yield return new Found(c, null);
            if (c is RepeaterDef r)
                foreach (var t in r.Template)
                    yield return new Found(t, r);
        }
    }
}
