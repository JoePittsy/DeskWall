using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model.Widgets;

/// <summary>Lays widget copies out as a single vertical stack in the right-hand column, by moving
/// each copy's origin. Only the starter generator arranges now: the designer's canvas is free
/// placement, and the column survives there only as the spawn region (<see cref="Column"/>).</summary>
public static class Arranger
{
    public const int ColumnX = 3220, ColumnWidth = 172, TopY = 40, BottomY = 1400, Gap = 16;

    /// <summary>The canvas the constants above were measured on. Any other display scales from it.</summary>
    public const int ReferenceWidth = 3440, ReferenceHeight = 1440;

    /// <summary>Stacks copies in `order` (copy ids): anchor top downwards from TopY, anchor bottom
    /// upwards from BottomY, by each copy's expanded bounds; writes the copies' X and Y.</summary>
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
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(order);
        if (layout.Copies is null) return;
        WidgetTemplate? Find(string key) => catalog.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        // One expansion: moving a copy moves its bounds by the same amount, so each copy's bounds
        // relative to its own origin hold for the whole stack.
        var expanded = WidgetExpander.Expand(layout, Find);
        var column = Column(canvasWidth, canvasHeight);
        var gap = Math.Max(4, (int)Math.Round(Gap * (canvasHeight / (double)ReferenceHeight)));
        var top = column.Y;
        var bottom = column.Bottom;
        foreach (var copyId in order)
        {
            if (Copies.Find(layout, copyId) is not { } copy) continue;
            var anchorBottom = string.Equals(Find(copy.Widget)?.Anchor, "bottom", StringComparison.OrdinalIgnoreCase);

            var bounds = Copies.Bounds(copy, expanded, Find);
            if (bounds.W == 0 && bounds.H == 0) continue;

            var newY = anchorBottom ? bottom - bounds.H : top;
            copy.X += column.X - bounds.X;
            copy.Y += newY - bounds.Y;

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
}
