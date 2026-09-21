using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>One thing the canvas treats as a unit: a widget instance together with every component
/// it owns, or a loose component that belongs to no widget (everything in a layout written before
/// widgets existed, and anything the owner has added by hand since).
/// <para>The canvas selects, drags, aligns, nudges and scales targets, never bare components:
/// moving half a clock is not something anyone means to do. Details, in the knobs panel, is still
/// where a single component inside a widget is reached.</para></summary>
public sealed record Target(string Id, bool IsWidget, IReadOnlyList<string> ComponentIds, Rect Bounds);

/// <summary>Reads a <see cref="LayoutFile"/> as the list of things the canvas can grab. Pure, so
/// hit-testing, rubber-band selection and "what is selected" are all testable without a window.</summary>
public static class Targets
{
    /// <summary>Everything a click can land on, in the layout's own component order. Zero-sized
    /// things are left out: they cannot be hit, and an outline round nothing says nothing.</summary>
    public static IReadOnlyList<Target> All(LayoutFile layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var list = new List<Target>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in layout.Components)
        {
            if (c.Widget is not { Length: > 0 } widget)
            {
                if (c.Rect.W > 0 && c.Rect.H > 0) list.Add(new Target(c.Id, false, [c.Id], c.Rect));
                continue;
            }
            if (!seen.Add(widget)) continue;
            var bounds = WidgetInstance.Bounds(layout, widget);
            if (bounds.W <= 0 || bounds.H <= 0) continue;
            var ids = layout.Components.Where(o => o.Widget == widget).Select(o => o.Id).ToList();
            list.Add(new Target(widget, true, ids, bounds));
        }
        return list;
    }

    /// <summary>The targets the given component ids belong to. The selection is kept as component
    /// ids - that is what the properties panel edits and what undo prunes - and this is how the
    /// canvas reads it back as things to move.</summary>
    public static IReadOnlyList<Target> From(LayoutFile layout, IEnumerable<string> componentIds)
    {
        ArgumentNullException.ThrowIfNull(componentIds);
        var wanted = componentIds.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return [];
        return All(layout).Where(t => t.ComponentIds.Any(wanted.Contains)).ToList();
    }

    /// <summary>The topmost target under a canvas point, or null for empty wallpaper. Later wins:
    /// the list is in paint order, so the last match is the one the eye sees on top.</summary>
    public static Target? Hit(IReadOnlyList<Target> targets, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Target? best = null;
        foreach (var t in targets)
            if (x >= t.Bounds.X && x < t.Bounds.Right && y >= t.Bounds.Y && y < t.Bounds.Bottom) best = t;
        return best;
    }

    /// <summary>Every target the rubber band touches. Intersection, not containment: at fit-to-
    /// window a 3440 px canvas is a third of its size on screen, and a band that has to swallow a
    /// widget whole is a band that keeps missing.</summary>
    public static IReadOnlyList<Target> Within(IReadOnlyList<Target> targets, Rect band)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Where(t => t.Bounds.Intersects(band)).ToList();
    }

    /// <summary>Every component id in the targets, in order, with no duplicates. What goes to
    /// <c>DesignerModel.Select</c>.</summary>
    public static IReadOnlyList<string> ComponentIds(IEnumerable<Target> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in targets)
            foreach (var id in t.ComponentIds)
                if (seen.Add(id)) ids.Add(id);
        return ids;
    }
}
