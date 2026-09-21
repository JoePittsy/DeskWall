using DeskWall.Core;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>The column. Widgets are a vertical stack on the right-hand margin; the owner chooses
/// the order, never the coordinates. Top-anchored widgets stack downwards from the top of the
/// column, bottom-anchored ones upwards from the bottom, so "drives" stays at the foot of the
/// screen however many widgets are above it.
/// <para>What the arranger writes is an ordinary layout: plain rects on plain components. The
/// daemon knows nothing about any of this.</para></summary>
public static class Arranger
{
    public const int ColumnX = 3220, ColumnWidth = 172, TopY = 40, BottomY = 1400, Gap = 16;

    /// <summary>Lay the widgets out in <paramref name="order"/>. An instance whose record says
    /// Unlocked keeps whatever rect it has: that is the whole point of the switch.</summary>
    public static void Arrange(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(layout);
        int top = TopY, bottom = BottomY;
        foreach (var id in order)
        {
            if (layout.Widgets is null || !layout.Widgets.TryGetValue(id, out var record) || record.Unlocked) continue;
            var bounds = WidgetInstance.Bounds(layout, id);
            if (bounds.W <= 0 && bounds.H <= 0) continue;
            var template = catalog.FirstOrDefault(t => string.Equals(t.Key, record.Template, StringComparison.OrdinalIgnoreCase));
            var height = template?.Height ?? bounds.H;

            int y;
            if (string.Equals(template?.Anchor, "bottom", StringComparison.OrdinalIgnoreCase))
            {
                y = bottom - height;
                bottom = y - Gap;
            }
            else
            {
                y = top;
                top = y + height + Gap;
            }
            MoveTo(layout, id, ColumnX - bounds.X, y - bounds.Y);
        }
    }

    /// <summary>The order the layout is in now: top to bottom by where each instance sits, with
    /// unlocked widgets last so they never push an arranged one down the list.</summary>
    public static IReadOnlyList<string> Order(LayoutFile layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return WidgetInstance.Instances(layout)
            .Select(id => (Id: id, Y: WidgetInstance.Bounds(layout, id).Y,
                           Unlocked: layout.Widgets is not null && layout.Widgets.TryGetValue(id, out var r) && r.Unlocked))
            .OrderBy(e => e.Unlocked)
            .ThenBy(e => e.Y)
            .Select(e => e.Id)
            .ToList();
    }

    private static void MoveTo(LayoutFile layout, string instanceId, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        foreach (var c in WidgetInstance.Components(layout, instanceId)) c.Rect = c.Rect.Offset(dx, dy);
    }

    /// <summary>The column's rect on a canvas of this size, for the preview's frame and for the
    /// first widget's origin. Scaled when the layout is not the 3440-wide one the constants assume,
    /// so a 1920x1200 RDP session still puts the column on the right-hand margin.</summary>
    public static Rect Column(int canvasWidth, int canvasHeight)
    {
        var sx = canvasWidth / 3440.0;
        var sy = canvasHeight / 1440.0;
        return new Rect(
            (int)Math.Round(ColumnX * sx), (int)Math.Round(TopY * sy),
            (int)Math.Round(ColumnWidth * sx), (int)Math.Round((BottomY - TopY) * sy));
    }
}
