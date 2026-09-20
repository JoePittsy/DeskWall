using System.Runtime.CompilerServices;
using DeskWall.Core.Layout;

[assembly: InternalsVisibleTo("DeskWall.Designer.Tests")]

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

    /// <summary>A template child's id is only unique inside its own repeater: two repeaters can each
    /// own a "letter", and Find(layout, id) would hand both of them the first one. Every caller that
    /// knows which repeater it is editing resolves through this overload instead. A null parentId
    /// means "not a template child", and falls through to the whole-layout search.</summary>
    public static Found? Find(LayoutFile layout, string? parentId, string childId)
    {
        if (parentId is null) return Find(layout, childId);
        if (FindRepeater(layout, parentId) is not { } r) return null;
        foreach (var t in r.Template)
            if (t.Id == childId) return new Found(t, r);
        return null;
    }

    /// <summary>The top-level repeater with this id. Top-level ids are unique, so no parent needed.</summary>
    public static RepeaterDef? FindRepeater(LayoutFile layout, string id)
        => layout.Components.OfType<RepeaterDef>().FirstOrDefault(r => r.Id == id);

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
