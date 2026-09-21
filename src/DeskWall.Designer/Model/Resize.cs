using System.Globalization;
using DeskWall.Core;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Model;

/// <summary>The eight grips round a selection. Order is clockwise from the top left, which is the
/// order they are drawn and hit-tested in.</summary>
public enum Handle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// Scaling a selection by dragging one of its grips: the new bounding box, and what that box does
/// to everything inside it.
/// <para>
/// Pure and integer, like <see cref="Placement"/>, and for the same reason - a resize is four
/// decisions (which edges move, does the ratio hold, does the grid bite, is it still big enough)
/// and none of them should only be reachable through a mouse.
/// </para>
/// <para>
/// A resize always works on the <em>bounding box of the selection</em> and then maps every
/// component into the new box proportionally. One widget, a widget's worth of components, or six
/// widgets at once are therefore the same operation with different contents, which is why there
/// is no separate "group resize".
/// </para>
/// </summary>
public static class Resize
{
    /// <summary>No edge of a resized box may end up closer than this to its opposite. A component
    /// dragged to zero is a component that has vanished from the canvas with no way back to it
    /// except undo, and a negative one renders as nothing at all.</summary>
    public const int MinSize = 8;

    /// <summary>Text never scales below this. Anything smaller is unreadable on a wallpaper seen
    /// from a desk, so it is a value nobody meant to ask for.</summary>
    public const double MinFontSize = 4;

    public static bool IsCorner(Handle handle)
        => handle is Handle.TopLeft or Handle.TopRight or Handle.BottomRight or Handle.BottomLeft;

    public static bool MovesLeft(Handle handle)
        => handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft;

    public static bool MovesRight(Handle handle)
        => handle is Handle.TopRight or Handle.Right or Handle.BottomRight;

    public static bool MovesTop(Handle handle)
        => handle is Handle.TopLeft or Handle.Top or Handle.TopRight;

    public static bool MovesBottom(Handle handle)
        => handle is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;

    // ---- the new box --------------------------------------------------------------------------

    /// <summary>The bounding box a drag of <paramref name="handle"/> by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) canvas pixels produces.
    /// <para>A corner holds the box's aspect ratio and an edge does not, which is what every
    /// drawing program since MacPaint has done and so is the one thing nobody has to be told. The
    /// opposite corner or edge is the anchor: it never moves.</para>
    /// <para>With snapping on, the edges the handle moves are put on the grid first and the ratio
    /// is then restored from them, so a corner drag lands one axis exactly on a grid line and
    /// derives the other from the ratio. Doing it the other way round would land on the grid and
    /// silently distort the thing being scaled, which is the worse of the two.</para></summary>
    public static Rect Box(Rect start, Handle handle, int dx, int dy, int? snapTo)
    {
        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;

        if (MovesLeft(handle)) left += dx;
        if (MovesRight(handle)) right += dx;
        if (MovesTop(handle)) top += dy;
        if (MovesBottom(handle)) bottom += dy;

        if (snapTo is { } spacing && spacing > 1)
        {
            if (MovesLeft(handle)) left = Placement.Snap(left, spacing);
            if (MovesRight(handle)) right = Placement.Snap(right, spacing);
            if (MovesTop(handle)) top = Placement.Snap(top, spacing);
            if (MovesBottom(handle)) bottom = Placement.Snap(bottom, spacing);
        }

        var w = Math.Max(MinSize, right - left);
        var h = Math.Max(MinSize, bottom - top);

        if (IsCorner(handle) && start.W > 0 && start.H > 0)
        {
            // Whichever axis the pointer has moved further along leads, and the other follows it.
            // Judging by the resulting aspect instead would leave a mostly-sideways drag on the
            // narrow corner of a wide box doing nothing at all, which reads as the drag being
            // ignored.
            var ratio = start.W / (double)start.H;
            if (Math.Abs(w - start.W) >= Math.Abs(h - start.H)) h = Math.Max(MinSize, (int)Math.Round(w / ratio));
            else w = Math.Max(MinSize, (int)Math.Round(h * ratio));
        }

        // The clamps and the ratio both change a size, and a size change has to come out of the
        // edge being dragged - taking it out of the anchored edge would slide the whole thing.
        if (MovesLeft(handle)) left = right - w;
        if (MovesTop(handle)) top = bottom - h;
        return new Rect(left, top, w, h);
    }

    // ---- what the new box does to what is in it ---------------------------------------------------

    /// <summary>Where <paramref name="r"/> ends up when the box round it goes from
    /// <paramref name="from"/> to <paramref name="to"/>: its position and its size both scale, so
    /// the parts of a widget keep their proportions and their spacing relative to each other.</summary>
    public static Rect Map(Rect r, Rect from, Rect to)
    {
        if (from.W <= 0 || from.H <= 0) return r;
        var sx = to.W / (double)from.W;
        var sy = to.H / (double)from.H;
        return new Rect(
            to.X + (int)Math.Round((r.X - from.X) * sx),
            to.Y + (int)Math.Round((r.Y - from.Y) * sy),
            Math.Max(1, (int)Math.Round(r.W * sx)),
            Math.Max(1, (int)Math.Round(r.H * sy)));
    }

    /// <summary>The factor a pixel-sized property inside the box is multiplied by. The height
    /// ratio, because a font size is a height and so is a dial's stroke; on a corner drag the two
    /// ratios are equal anyway, and a corner drag is the only one that scales anything but
    /// rects.</summary>
    public static double Scale(Rect from, Rect to)
        => from.H <= 0 ? 1 : to.H / (double)from.H;

    /// <summary>A pixel size scaled and rounded to a whole number. Whole, not exact: the layout
    /// stores these as numbers and "23.999999999999996" in a hand-readable JSON file is noise
    /// nobody asked for. Never below <paramref name="floor"/>.</summary>
    public static double ScaleSize(double value, double scale, double floor)
        => Math.Max(floor, Math.Round(value * scale, MidpointRounding.AwayFromZero));

    /// <summary>Put one component through a box change: its rect always, and - when
    /// <paramref name="scaleSizes"/> is set, which is to say on a corner drag - the pixel sizes
    /// inside it that would otherwise leave a widget scaled to twice the size still drawing
    /// 16 pt text in a 6 px arc.
    /// <para>Only literal values are touched. A bound size is a value the layout works out at
    /// paint time, and multiplying a binding by 1.4 is not a thing that can be written down.</para></summary>
    public static void Apply(ComponentDef component, Rect from, Rect to, bool scaleSizes)
    {
        ArgumentNullException.ThrowIfNull(component);
        component.Rect = Map(component.Rect, from, to);
        if (!scaleSizes) return;

        var scale = Scale(from, to);
        switch (component)
        {
            case TextDef text:
                text.Size = Scaled(text.Size, scale, MinFontSize);
                break;
            case DialDef dial:
                // A dial scaled up with its stroke left behind reads as a different widget, not a
                // bigger one; the arc IS the dial.
                dial.Thickness = Scaled(dial.Thickness, scale, 1);
                break;
            case RepeaterDef repeater:
                // The template's rects are cell-relative, so they scale in place rather than
                // being mapped into the new box; the cell and the gap between cells scale too, or
                // the rows drift apart as the list grows.
                var sx = from.W <= 0 ? 1 : to.W / (double)from.W;
                var sy = from.H <= 0 ? 1 : to.H / (double)from.H;
                repeater.Gap = (int)Math.Round(repeater.Gap * sy);
                repeater.CellHeight = Scaled(repeater.CellHeight, sy, 1);
                foreach (var child in repeater.Template)
                {
                    var scaledChild = child.Rect.Scale(sx, sy);
                    Apply(child, child.Rect, scaledChild, true);
                }
                break;
        }
    }

    /// <summary>A literal number property, scaled. A bound one, or one holding something that is
    /// not a number (a repeater's "auto" cell height), comes back untouched.</summary>
    private static PropertyValue Scaled(PropertyValue value, double scale, double floor)
    {
        if (value.IsBound || value.LiteralText is not { } text) return value;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value;
        return PropertyValue.Literal(ScaleSize(number, scale, floor));
    }
}
