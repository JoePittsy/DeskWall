using DeskWall.Core;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>Lays widget instances out as a single vertical stack in the right-hand column. Free
/// placement is a per-instance opt-out (<see cref="WidgetRecord.Unlocked"/>), not a mode: an
/// unlocked instance is skipped entirely and keeps whatever rect it already has.</summary>
public static class Arranger
{
    public const int ColumnX = 3220, ColumnWidth = 172, TopY = 40, BottomY = 1400, Gap = 16;

    /// <summary>Stacks widgets in `order` (instance ids): anchor top downwards from TopY, anchor
    /// bottom upwards from BottomY; skips instances whose WidgetRecord.Unlocked is true; writes
    /// rects into the layout's components.</summary>
    public static void Arrange(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, IReadOnlyList<string> order)
    {
        if (layout.Widgets is null) return;
        var top = TopY;
        var bottom = BottomY;
        foreach (var instanceId in order)
        {
            if (!layout.Widgets.TryGetValue(instanceId, out var record) || record.Unlocked) continue;
            var template = catalog.FirstOrDefault(t => t.Key == record.Template);
            var anchorBottom = string.Equals(template?.Anchor, "bottom", StringComparison.OrdinalIgnoreCase);

            var bounds = WidgetInstance.Bounds(layout, instanceId);
            if (bounds.W == 0 && bounds.H == 0) continue;

            var newY = anchorBottom ? bottom - bounds.H : top;
            var dx = ColumnX - bounds.X;
            var dy = newY - bounds.Y;
            foreach (var c in WidgetInstance.Components(layout, instanceId)) c.Rect = c.Rect.Offset(dx, dy);

            if (anchorBottom) bottom -= bounds.H + Gap; else top += bounds.H + Gap;
        }
    }

    /// <summary>Current top-to-bottom order by Bounds().Y, unlocked last.</summary>
    public static IReadOnlyList<string> Order(LayoutFile layout)
    {
        if (layout.Widgets is null) return [];
        var locked = new List<(string Id, int Y)>();
        var unlocked = new List<(string Id, int Y)>();
        foreach (var id in layout.Widgets.Keys)
        {
            var y = WidgetInstance.Bounds(layout, id).Y;
            var target = layout.Widgets[id].Unlocked ? unlocked : locked;
            target.Add((id, y));
        }
        return locked.OrderBy(x => x.Y).Select(x => x.Id)
            .Concat(unlocked.OrderBy(x => x.Y).Select(x => x.Id))
            .ToList();
    }
}
