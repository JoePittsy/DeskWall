using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

namespace DeskWall.Designer.Tests;

/// <summary>Scaling by a grip: which edges move, when the ratio holds, what the grid does to it,
/// how small it is allowed to get, and what the new box does to the components inside it -
/// including the font size, which is the whole reason the owner asked for this.</summary>
public class ResizeTests
{
    private static readonly Rect Start = new(100, 100, 200, 100);   // ratio 2:1

    // ---- which edges move ----------------------------------------------------------------------

    [Fact]
    public void An_Edge_Grip_Moves_Only_Its_Own_Edge()
    {
        Assert.Equal(new Rect(100, 100, 250, 100), Resize.Box(Start, Handle.Right, 50, 30, null));
        Assert.Equal(new Rect(150, 100, 150, 100), Resize.Box(Start, Handle.Left, 50, 30, null));
        Assert.Equal(new Rect(100, 130, 200, 70), Resize.Box(Start, Handle.Top, 50, 30, null));
        Assert.Equal(new Rect(100, 100, 200, 130), Resize.Box(Start, Handle.Bottom, 50, 30, null));
    }

    [Fact]
    public void An_Edge_Grip_Does_Not_Hold_The_Ratio()
    {
        var box = Resize.Box(Start, Handle.Right, 200, 0, null);
        Assert.Equal(400, box.W);
        Assert.Equal(100, box.H);
    }

    // ---- the ratio -------------------------------------------------------------------------------

    [Fact]
    public void A_Corner_Grip_Holds_The_Ratio_And_Anchors_The_Opposite_Corner()
    {
        var box = Resize.Box(Start, Handle.BottomRight, 100, 0, null);
        Assert.Equal(new Rect(100, 100, 300, 150), box);                 // top-left has not moved
        Assert.Equal(Start.W / (double)Start.H, box.W / (double)box.H, 3);
    }

    /// <summary>A mostly-sideways drag on a corner has to do something. Judging by the resulting
    /// aspect instead of by how far the pointer went leaves this case standing still.</summary>
    [Fact]
    public void A_Sideways_Corner_Drag_Still_Scales()
    {
        var box = Resize.Box(Start, Handle.TopLeft, 100, 0, null);
        Assert.Equal(new Rect(200, 150, 100, 50), box);                  // bottom-right has not moved
        Assert.Equal(Start.Right, box.Right);
        Assert.Equal(Start.Bottom, box.Bottom);
    }

    [Fact]
    public void A_Mostly_Vertical_Corner_Drag_Lets_The_Height_Lead()
    {
        var box = Resize.Box(Start, Handle.BottomRight, 10, 100, null);
        Assert.Equal(200, box.H);
        Assert.Equal(400, box.W);
    }

    // ---- the grid ---------------------------------------------------------------------------------

    [Fact]
    public void A_Snapped_Edge_Drag_Puts_That_Edge_On_The_Grid()
    {
        // 300 + 7 = 307, nearest multiple of 8 is 304.
        var box = Resize.Box(Start, Handle.Right, 7, 0, 8);
        Assert.Equal(0, box.Right % 8);
        Assert.Equal(new Rect(100, 100, 204, 100), box);
    }

    [Fact]
    public void Snapping_A_Corner_Puts_The_Leading_Axis_On_The_Grid_And_Derives_The_Other()
    {
        // The width leads (it moves further), lands on a grid line, and the height follows the 2:1.
        var box = Resize.Box(Start, Handle.BottomRight, 101, 0, 8);
        Assert.Equal(0, box.Right % 8);
        Assert.Equal(box.W / 2, box.H);
    }

    [Fact]
    public void Not_Holding_The_Modifier_Means_No_Grid_At_All()
        => Assert.Equal(new Rect(100, 100, 207, 100), Resize.Box(Start, Handle.Right, 7, 0, null));

    // ---- the floor ----------------------------------------------------------------------------------

    [Fact]
    public void A_Box_Cannot_Be_Dragged_To_Nothing()
    {
        var box = Resize.Box(Start, Handle.Right, -500, 0, null);
        Assert.Equal(Resize.MinSize, box.W);
        Assert.Equal(100, box.X);                                        // the anchored edge held

        var flipped = Resize.Box(Start, Handle.Left, 500, 0, null);
        Assert.Equal(Resize.MinSize, flipped.W);
        Assert.Equal(Start.Right, flipped.Right);                        // and here it is the right one
    }

    [Fact]
    public void A_Corner_Cannot_Be_Dragged_To_Nothing_Either()
    {
        var box = Resize.Box(Start, Handle.BottomRight, -500, -500, null);
        Assert.True(box.W >= Resize.MinSize);
        Assert.True(box.H >= Resize.MinSize);
    }

    // ---- what the box does to what is in it -------------------------------------------------------------

    [Fact]
    public void Map_Scales_Both_Position_And_Size_Within_The_Box()
    {
        var from = new Rect(0, 0, 100, 100);
        var to = new Rect(50, 20, 200, 200);
        Assert.Equal(new Rect(70, 40, 20, 20), Resize.Map(new Rect(10, 10, 10, 10), from, to));
        Assert.Equal(to, Resize.Map(from, from, to));
    }

    /// <summary>A multi-selection scales as one box, and every part of it keeps where it was
    /// inside that box - which is the same code path a widget's own components go through.</summary>
    [Fact]
    public void A_Selection_Scales_Proportionally_Inside_Its_Box()
    {
        var from = new Rect(0, 0, 100, 100);
        var to = new Rect(0, 0, 200, 300);
        var a = Resize.Map(new Rect(0, 0, 40, 20), from, to);
        var b = Resize.Map(new Rect(60, 80, 40, 20), from, to);

        Assert.Equal(new Rect(0, 0, 80, 60), a);
        Assert.Equal(new Rect(120, 240, 80, 60), b);
        Assert.Equal(to.W, b.Right);                                     // still flush with the box
        Assert.Equal(to.H, b.Bottom);
    }

    [Fact]
    public void A_Component_Never_Maps_To_Nothing()
        => Assert.Equal(new Rect(0, 0, 1, 1), Resize.Map(new Rect(0, 0, 1, 1), new Rect(0, 0, 1000, 1000), new Rect(0, 0, 8, 8)));

    // ---- text ---------------------------------------------------------------------------------------------

    private static TextDef Text(double size) => new()
    {
        Id = "t",
        Rect = new Rect(0, 0, 100, 40),
        Text = PropertyValue.Literal("x"),
        Size = PropertyValue.Literal(size),
    };

    /// <summary>The owner's actual complaint, in one assertion: scaling a text component up has to
    /// take the font with it, or a bigger box is all he gets.</summary>
    [Fact]
    public void A_Corner_Drag_Takes_The_Font_Size_With_It()
    {
        var text = Text(16);
        Resize.Apply(text, new Rect(0, 0, 100, 40), new Rect(0, 0, 200, 80), scaleSizes: true);
        Assert.Equal(new Rect(0, 0, 200, 80), text.Rect);
        Assert.Equal("32", text.Size.LiteralText);
    }

    [Fact]
    public void An_Edge_Drag_Changes_The_Box_And_Leaves_The_Font_Alone()
    {
        var text = Text(16);
        Resize.Apply(text, new Rect(0, 0, 100, 40), new Rect(0, 0, 300, 40), scaleSizes: false);
        Assert.Equal(new Rect(0, 0, 300, 40), text.Rect);
        Assert.Equal("16", text.Size.LiteralText);
    }

    /// <summary>A whole number, not 23.999999999999996: the layout is a file people read.</summary>
    [Fact]
    public void A_Scaled_Font_Size_Is_A_Round_Number()
    {
        var text = Text(16);
        Resize.Apply(text, new Rect(0, 0, 100, 40), new Rect(0, 0, 150, 60), scaleSizes: true);
        Assert.Equal("24", text.Size.LiteralText);
        Assert.DoesNotContain(".", text.Size.LiteralText, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_Never_Scales_Below_The_Legibility_Floor()
    {
        var text = Text(8);
        Resize.Apply(text, new Rect(0, 0, 100, 400), new Rect(0, 0, 10, 40), scaleSizes: true);
        Assert.Equal(Resize.MinFontSize.ToString("R", System.Globalization.CultureInfo.InvariantCulture), text.Size.LiteralText);
    }

    /// <summary>A size the layout works out at paint time cannot be multiplied by 1.4 and written
    /// down, so it is left exactly as it was.</summary>
    [Fact]
    public void A_Bound_Font_Size_Is_Left_Alone()
    {
        var text = Text(16);
        text.Size = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("cfg.size"));
        Resize.Apply(text, new Rect(0, 0, 100, 40), new Rect(0, 0, 200, 80), scaleSizes: true);
        Assert.True(text.Size.IsBound);
    }

    [Fact]
    public void A_Dials_Stroke_Scales_With_It_Or_It_Reads_As_A_Different_Widget()
    {
        var dial = new DialDef
        {
            Id = "d",
            Rect = new Rect(0, 0, 60, 60),
            Fraction = PropertyValue.Literal(0.5),
            Thickness = PropertyValue.Literal(6),
        };
        Resize.Apply(dial, new Rect(0, 0, 60, 60), new Rect(0, 0, 120, 120), scaleSizes: true);
        Assert.Equal("12", dial.Thickness.LiteralText);
    }

    /// <summary>A repeater's template lives in cell coordinates, so it scales in place rather than
    /// being mapped into the new box; the gap between cells goes with it.</summary>
    [Fact]
    public void A_Repeaters_Template_And_Gap_Scale_With_It()
    {
        var repeater = new RepeaterDef
        {
            Id = "drives",
            Rect = new Rect(0, 0, 100, 200),
            Items = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("disks.drives")),
            Gap = 10,
            CellHeight = PropertyValue.Literal(40),
            Template = [Text(12)],
        };
        Resize.Apply(repeater, new Rect(0, 0, 100, 200), new Rect(0, 0, 200, 400), scaleSizes: true);

        Assert.Equal(new Rect(0, 0, 200, 400), repeater.Rect);
        Assert.Equal(20, repeater.Gap);
        Assert.Equal("80", repeater.CellHeight.LiteralText);
        Assert.Equal(new Rect(0, 0, 200, 80), repeater.Template[0].Rect);
        Assert.Equal("24", ((TextDef)repeater.Template[0]).Size.LiteralText);
    }

    [Fact]
    public void An_Auto_Cell_Height_Is_Not_A_Number_And_Survives_Untouched()
    {
        var repeater = new RepeaterDef
        {
            Id = "drives",
            Rect = new Rect(0, 0, 100, 200),
            Items = PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("disks.drives")),
            CellHeight = PropertyValue.Literal("auto"),
            Template = [],
        };
        Resize.Apply(repeater, new Rect(0, 0, 100, 200), new Rect(0, 0, 200, 400), scaleSizes: true);
        Assert.Equal("auto", repeater.CellHeight.LiteralText);
    }
}
