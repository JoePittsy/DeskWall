using DeskWall.Designer.Model;
using Xunit;

namespace DeskWall.Designer.Tests;

/// <summary>Where a dragged widget lands. This is the whole of the reorder gesture that is not
/// WPF, and the arithmetic is the difference between "it went where I dropped it" and "the column
/// reshuffled itself", so it is tested rather than felt.
/// <para>The stack used below is the shipped column at a glance: clock 40..118, weather 134..194,
/// dial 210..290, drives 1308..1400.</para></summary>
public class ReorderTests
{
    private static readonly Reorder.Slot[] Column =
    [
        new("clock-1", 40, 118),
        new("weather-1", 134, 194),
        new("dial-1", 210, 290),
        new("drives-1", 1308, 1400),
    ];

    [Fact]
    public void Dropping_Above_Everything_Is_Index_Zero()
        => Assert.Equal(0, Reorder.IndexFor(Column, "drives-1", 0));

    [Fact]
    public void Dropping_Below_Everything_Is_The_End()
        => Assert.Equal(3, Reorder.IndexFor(Column, "clock-1", 1400));

    [Theory]
    // The dragged widget is taken out of the list first, so the index counts only the others.
    [InlineData(0, 0)]        // above the clock
    [InlineData(100, 1)]      // past the clock's centre (79)
    [InlineData(170, 2)]      // past the weather's centre (164)
    [InlineData(300, 3)]      // past the dial's centre (250)
    public void Index_Counts_The_Remaining_Widgets_Whose_Centre_Is_Above_The_Pointer(int pointerY, int expected)
        => Assert.Equal(expected, Reorder.IndexFor(Column, "drives-1", pointerY));

    [Fact]
    public void The_Dragged_Widget_Never_Counts_Against_Itself()
    {
        // Dropped exactly where it already is: the clock's own centre must not push the index on.
        Assert.Equal(0, Reorder.IndexFor(Column, "clock-1", 79));
        Assert.False(Reorder.Changes(["clock-1", "weather-1", "dial-1", "drives-1"], "clock-1", 0));
    }

    [Fact]
    public void An_Edge_Is_Not_A_Centre()
    {
        // The clock's bottom edge is 118 but its centre is 79, so a drop at 100 - inside the clock,
        // below its middle - already belongs after it. Edges would need the whole widget cleared.
        Assert.Equal(1, Reorder.IndexFor(Column, "drives-1", 100));
    }

    [Fact]
    public void Move_Takes_The_Widget_Out_Before_Putting_It_Back()
    {
        string[] order = ["clock-1", "weather-1", "dial-1", "drives-1"];
        Assert.Equal(["weather-1", "dial-1", "clock-1", "drives-1"], Reorder.Move(order, "clock-1", 2));
        Assert.Equal(["drives-1", "clock-1", "weather-1", "dial-1"], Reorder.Move(order, "drives-1", 0));
    }

    [Fact]
    public void Move_Clamps_An_Index_Past_The_End_Rather_Than_Throwing()
        => Assert.Equal(["weather-1", "clock-1"], Reorder.Move(["clock-1", "weather-1"], "clock-1", 99));

    [Fact]
    public void Move_Leaves_An_Order_That_Does_Not_Contain_The_Widget_Alone()
    {
        string[] order = ["clock-1", "weather-1"];
        Assert.Same(order, Reorder.Move(order, "nothing-9", 0));
    }

    [Fact]
    public void Changes_Is_False_When_The_Drop_Puts_It_Back_Where_It_Was()
    {
        string[] order = ["clock-1", "weather-1", "dial-1"];
        Assert.False(Reorder.Changes(order, "weather-1", 1));
        Assert.True(Reorder.Changes(order, "weather-1", 2));
    }

    [Fact]
    public void A_Drag_Round_Trips_Through_IndexFor_And_Move()
    {
        var order = Column.Select(s => s.Id).ToArray();
        // Drag the clock down to just past the dial's centre.
        var index = Reorder.IndexFor(Column, "clock-1", 260);
        Assert.Equal(["weather-1", "dial-1", "clock-1", "drives-1"], Reorder.Move(order, "clock-1", index));
    }

    [Fact]
    public void An_Empty_Stack_Has_One_Place_To_Drop()
        => Assert.Equal(0, Reorder.IndexFor([], "clock-1", 500));
}
