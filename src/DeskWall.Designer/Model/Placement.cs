using DeskWall.Core;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>Which line of the selection's own bounding box things are brought onto.</summary>
public enum AlignOp
{
    Left,
    CentreX,
    Right,
    Top,
    MiddleY,
    Bottom,
}

/// <summary>Which way the space between selected things is evened out.</summary>
public enum DistributeAxis
{
    Horizontal,
    Vertical,
}

/// <summary>
/// The arithmetic behind direct manipulation on the canvas: where a widget added from the gallery
/// lands, what the grid rounds a drag to, and what align and distribute do to a selection.
/// <para>
/// All of it is pure, integer and in canvas pixels. The view's job is to turn a pointer into a
/// delta; every decision after that belongs here, where a test can reach it without a window.
/// </para>
/// <para>
/// Every operation answers with a per-target <em>offset</em> rather than a finished rect, because
/// a target is usually a widget - several components that have to move as one - and an offset is
/// the only answer that applies to all of them at once. It is also what keeps a multi-selection's
/// relative offsets exactly as they were: one number, applied to everything.
/// </para>
/// </summary>
public static class Placement
{
    /// <summary>The grid spacings the canvas offers. Multiples of 8: every metric the designer grew
    /// up with (a 172 px column, 40 px from the top, 16 px between widgets) is a multiple of 4, and
    /// 8 is the coarsest step that still lands on all of them.</summary>
    public static readonly IReadOnlyList<int> Spacings = [8, 16, 32];

    /// <summary>The spacing the canvas starts with.</summary>
    public const int DefaultSpacing = 8;

    /// <summary>How far a spawned widget steps down when there is no room below the last one. Far
    /// enough that the one underneath is still visible, near enough that a run of them stays in
    /// the margin rather than marching off the bottom.</summary>
    public const int CascadeStep = 24;

    /// <summary>The gap a spawned widget leaves below the lowest one already in the margin. The
    /// same number the old column stacker used, so a layout built by adding widgets one after
    /// another still looks deliberate rather than dropped.</summary>
    public const int SpawnGap = Arranger.Gap;

    // ---- boxes -------------------------------------------------------------------------------

    /// <summary>The smallest rect containing them all; a default rect when there are none.</summary>
    public static Rect Bounds(IEnumerable<Rect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        var any = false;
        int minX = 0, minY = 0, maxRight = 0, maxBottom = 0;
        foreach (var r in rects)
        {
            if (!any) { minX = r.X; minY = r.Y; maxRight = r.Right; maxBottom = r.Bottom; any = true; continue; }
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.Right > maxRight) maxRight = r.Right;
            if (r.Bottom > maxBottom) maxBottom = r.Bottom;
        }
        return any ? new Rect(minX, minY, maxRight - minX, maxBottom - minY) : default;
    }

    // ---- the grid ----------------------------------------------------------------------------

    /// <summary>The nearest grid line to <paramref name="value"/>. Halfway rounds away from zero so
    /// a value behaves the same either side of the origin; a spacing of 1 or less is no grid at
    /// all and leaves the value alone.</summary>
    public static int Snap(int value, int spacing)
        => spacing <= 1 ? value : (int)Math.Round(value / (double)spacing, MidpointRounding.AwayFromZero) * spacing;

    /// <summary>The offset a drag actually applies: the raw one unless snapping is on, in which
    /// case the <em>anchor's</em> top-left lands on the grid and everything being dragged takes
    /// that same offset.
    /// <para>Snapping is per-drag and opt-in (hold the modifier), the opposite way round from
    /// Figma. Deliberate: on a 3440 px wallpaper most placement is judged by eye against a
    /// photograph, and a grid that is always on is a grid always fighting the eye.</para></summary>
    public static (int Dx, int Dy) DragOffset(Rect anchor, int dx, int dy, int? snapTo)
    {
        if (snapTo is not { } spacing || spacing <= 1) return (dx, dy);
        return (Snap(anchor.X + dx, spacing) - anchor.X, Snap(anchor.Y + dy, spacing) - anchor.Y);
    }

    // ---- align and distribute ------------------------------------------------------------------

    /// <summary>Bring every target onto one line of the bounding box of them all, and say by how
    /// much each has to move to get there.
    /// <para>The bounding box, not the first-selected thing: that is what Figma does with a plain
    /// multi-selection, and it means the answer does not depend on click order, which nobody
    /// remembers. Fewer than two targets have nothing to line up with, so nothing moves.</para></summary>
    public static IReadOnlyList<(int Dx, int Dy)> Align(IReadOnlyList<Rect> targets, AlignOp op)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var offsets = new (int Dx, int Dy)[targets.Count];
        if (targets.Count < 2) return offsets;

        var box = Bounds(targets);
        for (var i = 0; i < targets.Count; i++)
        {
            var r = targets[i];
            offsets[i] = op switch
            {
                AlignOp.Left => (box.X - r.X, 0),
                AlignOp.CentreX => (box.X + (box.W - r.W) / 2 - r.X, 0),
                AlignOp.Right => (box.Right - r.W - r.X, 0),
                AlignOp.Top => (0, box.Y - r.Y),
                AlignOp.MiddleY => (0, box.Y + (box.H - r.H) / 2 - r.Y),
                AlignOp.Bottom => (0, box.Bottom - r.H - r.Y),
                _ => (0, 0),
            };
        }
        return offsets;
    }

    /// <summary>Even out the space between the targets along one axis, holding the two outermost
    /// where they are.
    /// <para>Equal gaps, not equal centres. With mixed sizes - and a column of a clock, a dial and
    /// a drive list is nothing but mixed sizes - equal centres leaves visibly unequal space, and
    /// space is the thing the eye actually reads. Fewer than three targets cannot have an uneven
    /// gap, so nothing moves.</para></summary>
    public static IReadOnlyList<(int Dx, int Dy)> Distribute(IReadOnlyList<Rect> targets, DistributeAxis axis)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var offsets = new (int Dx, int Dy)[targets.Count];
        if (targets.Count < 3) return offsets;

        var horizontal = axis == DistributeAxis.Horizontal;
        var order = Enumerable.Range(0, targets.Count)
            .OrderBy(i => horizontal ? targets[i].X : targets[i].Y)
            .ToList();

        var box = Bounds(targets);
        var extent = horizontal ? box.W : box.H;
        var used = targets.Sum(r => horizontal ? r.W : r.H);
        var free = extent - used;
        var gaps = targets.Count - 1;

        // Each position is the running total of the sizes so far plus a share of the free space
        // computed from scratch, not a gap added on each time round: an integer gap would drift by
        // up to one pixel per item, which over a column of eight is a visibly crooked last one.
        var run = horizontal ? box.X : box.Y;
        for (var k = 0; k < order.Count; k++)
        {
            var i = order[k];
            var r = targets[i];
            var want = run + (int)Math.Round(free * k / (double)gaps, MidpointRounding.AwayFromZero);
            offsets[i] = horizontal ? (want - r.X, 0) : (0, want - r.Y);
            run += horizontal ? r.W : r.H;
        }
        return offsets;
    }

    // ---- where a new widget lands ----------------------------------------------------------------

    /// <summary>Where a widget just picked from the gallery goes: in <paramref name="region"/>, the
    /// gap below whatever is already in it.
    /// <para>The right-hand margin rather than the middle of the canvas, because windows sit
    /// centred on the ultrawide and leave that margin free - it is the only part of the wallpaper
    /// that is reliably visible (CLAUDE.md). This is a landing spot and nothing more: the widget is
    /// free to drag the instant it appears.</para>
    /// <para>When there is no room left below, it cascades down from the top of the region instead,
    /// stepping past anything already at that exact spot. Overlapping something is recoverable;
    /// landing off the bottom edge, where the owner never sees it, is not.</para></summary>
    public static Rect Spawn(Rect region, IReadOnlyList<Rect> existing, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(existing);

        // Only what is actually in the margin counts as "already there": a widget the owner has
        // dragged over to the left of the wallpaper says nothing about where the next one belongs.
        var inRegion = existing.Where(r => r.X < region.Right && region.X < r.Right).ToList();
        var y = inRegion.Count == 0 ? region.Y : inRegion.Max(r => r.Bottom) + SpawnGap;

        if (y + height > region.Bottom)
        {
            y = region.Y;
            while (y + height + CascadeStep <= region.Bottom
                   && existing.Any(r => r.X == region.X && r.Y == y))
            {
                y += CascadeStep;
            }
        }
        return new Rect(region.X, y, width, height);
    }
}
