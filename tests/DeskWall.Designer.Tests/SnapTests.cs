using DeskWall.Core;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 3.2: the smart-guide maths the canvas now calls on every move and resize. (The
/// older SnapTests class, in DesignerModelTests.cs, covers Lines and Edge on their own.)</summary>
public class SmartGuideTests
{
    private static readonly Rect Canvas = new(0, 0, 1000, 600);
    private static readonly Rect Other = new(400, 100, 100, 50);   // edges x 400/450/500, y 100/125/150

    [Fact]
    public void A_Move_Within_The_Threshold_Lands_On_A_Siblings_Edge_With_A_Guide()
    {
        var (snapped, guides) = Snap.Apply(new Rect(503, 300, 60, 40), [Other], Canvas);
        Assert.Equal(new Rect(500, 300, 60, 40), snapped);
        Assert.Equal(new Snap.Guide(true, 500), Assert.Single(guides));
    }

    [Fact]
    public void Centres_Snap_To_Centres_And_Both_Axes_At_Once()
    {
        // Moving centre (452, 127) is 2 from the other's centre lines on both axes.
        var (snapped, guides) = Snap.Apply(new Rect(422, 107, 60, 40), [Other], Canvas);
        Assert.Equal(new Rect(420, 105, 60, 40), snapped);
        Assert.Equal(2, guides.Count);
        Assert.Contains(new Snap.Guide(true, 450), guides);
        Assert.Contains(new Snap.Guide(false, 125), guides);
    }

    [Fact]
    public void Beyond_The_Threshold_Nothing_Moves()
    {
        var moving = new Rect(510, 300, 60, 40);
        var (snapped, guides) = Snap.Apply(moving, [Other], Canvas, threshold: 6);
        Assert.Equal(moving, snapped);
        Assert.Empty(guides);
        // The canvas divides the threshold by its zoom: at 1/4 the same six screen pixels are 24.
        Assert.Equal(new Rect(500, 300, 60, 40), Snap.Apply(moving, [Other], Canvas, threshold: 24).Snapped);
    }

    [Fact]
    public void The_Canvas_Edges_Are_Lines_Too()
    {
        var (snapped, _) = Snap.Apply(new Rect(4, 555, 60, 40), [], Canvas);
        Assert.Equal(new Rect(0, 560, 60, 40), snapped);
    }

    [Fact]
    public void An_Edge_Resize_Snaps_Only_The_Moving_Edge()
    {
        var start = new Rect(100, 300, 200, 50);
        var box = new Rect(100, 300, 297, 50);          // right edge dragged to 397, 3 short of 400
        var (snapped, guides) = Snap.Resize(start, box, Handle.Right, [Other], Canvas);
        Assert.Equal(new Rect(100, 300, 300, 50), snapped);
        Assert.Equal(new Snap.Guide(true, 400), Assert.Single(guides));
    }

    [Fact]
    public void A_Left_Edge_Resize_Keeps_The_Right_Edge_Anchored()
    {
        var start = new Rect(520, 300, 100, 50);
        var box = new Rect(503, 300, 117, 50);
        var (snapped, _) = Snap.Resize(start, box, Handle.Left, [Other], Canvas);
        Assert.Equal(new Rect(500, 300, 120, 50), snapped);
    }

    [Fact]
    public void A_Corner_Resize_Keeps_The_Ratio_From_The_Axis_That_Snapped()
    {
        var start = new Rect(100, 200, 200, 100);       // 2:1
        var box = new Rect(100, 200, 298, 149);         // bottom-right dragged near x 400
        var (snapped, guides) = Snap.Resize(start, box, Handle.BottomRight, [Other], Canvas);
        Assert.Equal(new Rect(100, 200, 300, 150), snapped);
        Assert.Equal(new Snap.Guide(true, 400), Assert.Single(guides));
    }

    [Fact]
    public void A_Corner_Resize_Near_Nothing_Is_Left_Alone()
    {
        var start = new Rect(100, 200, 200, 100);
        var box = new Rect(100, 200, 260, 130);
        var (snapped, guides) = Snap.Resize(start, box, Handle.BottomRight, [Other], Canvas);
        Assert.Equal(box, snapped);
        Assert.Empty(guides);
    }
}
