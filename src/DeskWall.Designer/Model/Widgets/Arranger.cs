using DeskWall.Core;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>Lays widget instances out as a single vertical stack in the right-hand column. Free
/// placement is a per-instance opt-out (<see cref="WidgetRecord.Unlocked"/>), not a mode: an
/// unlocked instance is skipped entirely and keeps whatever rect it already has.</summary>
public static class Arranger
{
    public const int ColumnX = 3220, ColumnWidth = 172, TopY = 40, BottomY = 1400, Gap = 16;

    /// <summary>The canvas the constants above were measured on. Any other display scales from it.</summary>
    public const int ReferenceWidth = 3440, ReferenceHeight = 1440;

    /// <summary>Stacks widgets in `order` (instance ids): anchor top downwards from TopY, anchor
    /// bottom upwards from BottomY; skips instances whose WidgetRecord.Unlocked is true; writes
    /// rects into the layout's components.</summary>
    public static void Arrange(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, IReadOnlyList<string> order)
        => Arrange(layout, catalog, order, ReferenceWidth, ReferenceHeight);

    /// <summary>The same, on a canvas that is not the ultrawide these constants were measured on.
    /// <para>Needed because x 3220 is off the right-hand edge of every display narrower than 3392:
    /// an RDP session reports 1920x1200 and would otherwise get a column nobody can see (doctrine
    /// gate 7). Only the column's own geometry moves - the widgets' footprints are not scaled, since
    /// that is <c>LayoutScaler</c>'s job and it has already run by the time a layout is open.</para></summary>
    public static void Arrange(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, IReadOnlyList<string> order,
        int canvasWidth, int canvasHeight)
    {
        if (layout.Widgets is null) return;
        var column = Column(canvasWidth, canvasHeight);
        var gap = Math.Max(4, (int)Math.Round(Gap * (canvasHeight / (double)ReferenceHeight)));
        var top = column.Y;
        var bottom = column.Bottom;
        foreach (var instanceId in order)
        {
            if (!layout.Widgets.TryGetValue(instanceId, out var record) || record.Unlocked) continue;
            var template = catalog.FirstOrDefault(t => t.Key == record.Template);
            var anchorBottom = string.Equals(template?.Anchor, "bottom", StringComparison.OrdinalIgnoreCase);

            var bounds = WidgetInstance.Bounds(layout, instanceId);
            if (bounds.W == 0 && bounds.H == 0) continue;

            var newY = anchorBottom ? bottom - bounds.H : top;
            var dx = column.X - bounds.X;
            var dy = newY - bounds.Y;
            foreach (var c in WidgetInstance.Components(layout, instanceId)) c.Rect = c.Rect.Offset(dx, dy);

            if (anchorBottom) bottom -= bounds.H + gap; else top += bounds.H + gap;
        }
    }

    /// <summary>The column on a canvas of this size: the same 172 px of screen, the same distance in
    /// from the right-hand edge, running from TopY to BottomY scaled down the canvas. The preview
    /// draws this and <see cref="Arrange"/> stacks into it.
    /// <para>The width does not scale. A widget's components are 172 px wide whatever display the
    /// layout is open on, so a column scaled to 85 px on a 1692-wide session would put half of every
    /// widget off the right-hand edge. Right-aligning it instead keeps "right-hand margin only" true
    /// on every display, which is the rule that matters.</para></summary>
    public static Rect Column(int canvasWidth, int canvasHeight)
    {
        var rightMargin = (int)Math.Round((ReferenceWidth - ColumnX - ColumnWidth) * (canvasWidth / (double)ReferenceWidth));
        var sy = canvasHeight / (double)ReferenceHeight;
        var x = Math.Max(0, canvasWidth - ColumnWidth - rightMargin);
        return new Rect(x, (int)Math.Round(TopY * sy), ColumnWidth, (int)Math.Round((BottomY - TopY) * sy));
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
