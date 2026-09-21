using DeskWall.Core;
using DeskWall.Designer.Model;
using Xunit;

namespace DeskWall.Designer.Tests;

/// <summary>The canvas's arithmetic, without a canvas: grid snapping, the selection's bounding
/// box, each align and distribute operation, group-drag offsets and where a new widget lands.</summary>
public class PlacementTests
{
    // ---- bounding box -------------------------------------------------------------------------

    [Fact]
    public void Bounds_Is_The_Union_Of_Them_All()
    {
        var box = Placement.Bounds([new Rect(100, 50, 40, 10), new Rect(60, 80, 20, 100), new Rect(90, 60, 10, 10)]);
        Assert.Equal(new Rect(60, 50, 80, 130), box);
    }

    [Fact]
    public void Bounds_Of_Nothing_Is_Empty()
        => Assert.Equal(default, Placement.Bounds([]));

    [Fact]
    public void Bounds_Of_One_Is_That_One()
        => Assert.Equal(new Rect(3, 4, 5, 6), Placement.Bounds([new Rect(3, 4, 5, 6)]));

    // ---- the grid ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(3, 8, 0)]
    [InlineData(4, 8, 8)]        // halfway rounds away from zero
    [InlineData(11, 8, 8)]
    [InlineData(12, 8, 16)]
    [InlineData(-4, 8, -8)]      // and does so on the other side of the origin too
    [InlineData(-11, 8, -8)]
    [InlineData(33, 16, 32)]
    [InlineData(40, 16, 48)]
    public void Snap_Rounds_To_The_Nearest_Line(int value, int spacing, int expected)
        => Assert.Equal(expected, Placement.Snap(value, spacing));

    [Fact]
    public void Snap_With_No_Real_Spacing_Leaves_The_Value_Alone()
    {
        Assert.Equal(37, Placement.Snap(37, 1));
        Assert.Equal(37, Placement.Snap(37, 0));
    }

    [Fact]
    public void A_Drag_Without_The_Modifier_Is_Pixel_Exact()
        => Assert.Equal((13, -7), Placement.DragOffset(new Rect(103, 57, 50, 50), 13, -7, null));

    [Fact]
    public void A_Snapped_Drag_Puts_The_Anchors_Top_Left_On_The_Grid()
    {
        // 103 + 10 = 113 -> 112; 57 + 10 = 67 -> 64.
        var (dx, dy) = Placement.DragOffset(new Rect(103, 57, 50, 50), 10, 10, 8);
        Assert.Equal((9, 7), (dx, dy));
        Assert.Equal(0, (103 + dx) % 8);
        Assert.Equal(0, (57 + dy) % 8);
    }

    /// <summary>The point of answering with one offset rather than a rect each: everything in a
    /// multi-selection takes the same offset, so the gaps between them survive the drag - snapped
    /// or not.</summary>
    [Fact]
    public void A_Group_Drag_Keeps_The_Relative_Offsets()
    {
        var a = new Rect(103, 57, 50, 50);
        var b = new Rect(211, 90, 30, 20);
        var anchor = Placement.Bounds([a, b]);

        var (dx, dy) = Placement.DragOffset(anchor, 10, 10, 8);
        var movedA = a.Offset(dx, dy);
        var movedB = b.Offset(dx, dy);

        Assert.Equal(b.X - a.X, movedB.X - movedA.X);
        Assert.Equal(b.Y - a.Y, movedB.Y - movedA.Y);
        Assert.Equal(0, (anchor.X + dx) % 8);
        Assert.Equal(0, (anchor.Y + dy) % 8);
    }

    // ---- align ----------------------------------------------------------------------------------

    // Bounding box (0, 0, 130, 60): A holds the top and the left, B the bottom and the right, so
    // every operation has something to say about both of them.
    private static readonly Rect A = new(0, 0, 100, 20);
    private static readonly Rect B = new(50, 50, 80, 10);

    [Theory]
    [InlineData(AlignOp.Left, 0, 0, -50, 0)]
    [InlineData(AlignOp.CentreX, 15, 0, -25, 0)]
    [InlineData(AlignOp.Right, 30, 0, 0, 0)]
    [InlineData(AlignOp.Top, 0, 0, 0, -50)]
    [InlineData(AlignOp.MiddleY, 0, 20, 0, -25)]
    [InlineData(AlignOp.Bottom, 0, 40, 0, 0)]
    public void Align_Moves_Onto_The_Selections_Own_Bounding_Box(AlignOp op, int adx, int ady, int bdx, int bdy)
    {
        var offsets = Placement.Align([A, B], op);
        Assert.Equal((adx, ady), offsets[0]);
        Assert.Equal((bdx, bdy), offsets[1]);
    }

    /// <summary>The bounding box, not the first-clicked thing: reversing the order must give the
    /// same answer, because nobody remembers what they clicked first.</summary>
    [Fact]
    public void Align_Does_Not_Depend_On_The_Order_Things_Were_Selected()
    {
        var forwards = Placement.Align([A, B], AlignOp.Right);
        var backwards = Placement.Align([B, A], AlignOp.Right);
        Assert.Equal(forwards[0], backwards[1]);
        Assert.Equal(forwards[1], backwards[0]);
    }

    [Fact]
    public void Align_Needs_Two_Things_To_Line_Up()
        => Assert.Equal((0, 0), Assert.Single(Placement.Align([A], AlignOp.Right)));

    // ---- distribute --------------------------------------------------------------------------------

    [Fact]
    public void Distribute_Evens_Out_The_Gaps_And_Holds_The_Outermost_Still()
    {
        IReadOnlyList<Rect> rects = [new(0, 0, 10, 10), new(20, 0, 10, 10), new(100, 0, 10, 10)];
        var offsets = Placement.Distribute(rects, DistributeAxis.Horizontal);
        var moved = rects.Select((r, i) => r.Offset(offsets[i].Dx, offsets[i].Dy)).ToList();

        Assert.Equal(0, moved[0].X);                      // the first stays put
        Assert.Equal(100, moved[2].X);                    // so does the last
        Assert.Equal(moved[1].X - moved[0].Right, moved[2].X - moved[1].Right);
    }

    [Fact]
    public void Distribute_Works_The_Other_Way_Up_Too()
    {
        // Mixed heights, which is the case equal centres would get visibly wrong.
        IReadOnlyList<Rect> rects = [new(0, 0, 10, 40), new(0, 60, 10, 10), new(0, 300, 10, 20)];
        var offsets = Placement.Distribute(rects, DistributeAxis.Vertical);
        var moved = rects.Select((r, i) => r.Offset(offsets[i].Dx, offsets[i].Dy)).ToList();

        Assert.Equal(0, moved[0].Y);
        Assert.Equal(300, moved[2].Y);
        Assert.Equal(moved[1].Y - moved[0].Bottom, moved[2].Y - moved[1].Bottom);
    }

    [Fact]
    public void Distribute_Sorts_By_Position_Not_By_Selection_Order()
    {
        IReadOnlyList<Rect> rects = [new(100, 0, 10, 10), new(0, 0, 10, 10), new(20, 0, 10, 10)];
        var offsets = Placement.Distribute(rects, DistributeAxis.Horizontal);
        var moved = rects.Select((r, i) => r.Offset(offsets[i].Dx, offsets[i].Dy)).ToList();

        Assert.Equal(0, moved[1].X);                      // leftmost, whatever index it had
        Assert.Equal(100, moved[0].X);                    // rightmost
        Assert.Equal(moved[2].X - moved[1].Right, moved[0].X - moved[2].Right);
    }

    [Fact]
    public void Two_Things_Cannot_Have_An_Uneven_Gap_So_Nothing_Moves()
        => Assert.All(Placement.Distribute([A, B], DistributeAxis.Horizontal), o => Assert.Equal((0, 0), o));

    // ---- where a new widget lands ---------------------------------------------------------------------

    private static readonly Rect Region = new(3220, 40, 172, 1360);

    [Fact]
    public void The_First_Widget_Lands_At_The_Top_Of_The_Region()
        => Assert.Equal(new Rect(3220, 40, 172, 60), Placement.Spawn(Region, [], 172, 60));

    [Fact]
    public void The_Next_One_Lands_Below_The_Lowest_With_The_Gap()
    {
        var at = Placement.Spawn(Region, [new Rect(3220, 40, 172, 78)], 172, 60);
        Assert.Equal(new Rect(3220, 40 + 78 + Placement.SpawnGap, 172, 60), at);
    }

    /// <summary>A widget dragged off to the left of the wallpaper says nothing about where the
    /// next one belongs, so it does not push the landing spot down the margin.</summary>
    [Fact]
    public void Something_Dragged_Out_Of_The_Region_Does_Not_Count_As_Being_In_It()
    {
        var at = Placement.Spawn(Region, [new Rect(100, 900, 172, 78)], 172, 60);
        Assert.Equal(40, at.Y);
    }

    [Fact]
    public void With_No_Room_Below_It_Cascades_From_The_Top_Instead_Of_Going_Off_The_Bottom()
    {
        var full = new Rect(3220, 40, 172, 1390);
        var at = Placement.Spawn(Region, [full], 172, 60);
        Assert.Equal(3220, at.X);
        Assert.Equal(40 + Placement.CascadeStep, at.Y);   // stepped past the one already at the top
        Assert.True(at.Bottom <= Region.Bottom);
    }

    [Fact]
    public void A_Cascade_Never_Lands_Exactly_On_Its_Predecessor()
    {
        var full = new Rect(3220, 40, 172, 1390);
        var first = Placement.Spawn(Region, [full], 172, 60);
        var second = Placement.Spawn(Region, [full, first], 172, 60);
        Assert.NotEqual(first.Y, second.Y);
    }
}
