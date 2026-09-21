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

    /// <summary>The canvas the constants above are written for. Any other display scales from it.</summary>
    public const int ReferenceWidth = 3440, ReferenceHeight = 1440;

    /// <summary>Lay the widgets out in <paramref name="order"/> on the reference canvas.</summary>
    public static void Arrange(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, IReadOnlyList<string> order)
        => Arrange(layout, catalog, order, ReferenceWidth, ReferenceHeight);

    /// <summary>Lay the widgets out in <paramref name="order"/>. An instance whose record says
    /// Unlocked keeps whatever rect it has: that is the whole point of the switch.
    /// <para>The column's own geometry is scaled to the canvas, because x 3220 is off the right-hand
    /// edge of every display narrower than the ultrawide these constants were measured on - an RDP
    /// session reports 1920x1200 and would otherwise get a column nobody can see (doctrine gate 7).
    /// The widgets' own footprints are not scaled: that is LayoutScaler's job and it already does
    /// it when a layout moves between displays.</para></summary>
    public static void Arrange(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, IReadOnlyList<string> order,
        int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var column = Column(canvasWidth, canvasHeight);
        var gap = Math.Max(4, (int)Math.Round(Gap * (canvasHeight / (double)ReferenceHeight)));
        int top = column.Y, bottom = column.Bottom;
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
                bottom = y - gap;
            }
            else
            {
                y = top;
                top = y + height + gap;
            }
            MoveTo(layout, id, column.X - bounds.X, y - bounds.Y);
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

    /// <summary>The column on a canvas of this size: the same 172 px of screen, the same distance in
    /// from the right-hand edge, running from TopY to BottomY scaled down the canvas.
    /// <para>The width does not scale. A widget's components are 172 px wide whatever display the
    /// layout is opened on (LayoutScaler is what resizes a layout between displays, and it has not
    /// run here), so a column scaled to 85 px on a 1692-wide RDP session would put half of every
    /// widget off the right-hand edge. Right-aligning it instead keeps the owner's "right-hand
    /// margin only" true on every display, which is the rule that matters.</para></summary>
    public static Rect Column(int canvasWidth, int canvasHeight)
    {
        var rightMargin = (int)Math.Round((ReferenceWidth - ColumnX - ColumnWidth) * (canvasWidth / (double)ReferenceWidth));
        var sy = canvasHeight / (double)ReferenceHeight;
        var x = Math.Max(0, canvasWidth - ColumnWidth - rightMargin);
        return new Rect(x, (int)Math.Round(TopY * sy), ColumnWidth, (int)Math.Round((BottomY - TopY) * sy));
    }
}
