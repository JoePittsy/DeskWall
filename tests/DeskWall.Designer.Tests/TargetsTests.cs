using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

namespace DeskWall.Designer.Tests;

/// <summary>Reading a layout as the things the canvas can grab: a widget is one target however
/// many components it owns, a hand-placed component is a target on its own, and hit-testing and
/// rubber-banding both answer in those terms.</summary>
public class TargetsTests
{
    /// <summary>Two components of one widget, a second widget, and a loose component from before
    /// widgets existed.</summary>
    private static LayoutFile Layout() => LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "text", "id": "clock-1.time", "widget": "clock-1", "rect": [100, 100, 80, 40], "text": "x" },
            { "type": "text", "id": "clock-1.date", "widget": "clock-1", "rect": [100, 140, 80, 20], "text": "x" },
            { "type": "bar",  "id": "dial-1.bar",   "widget": "dial-1",  "rect": [300, 100, 60, 10], "fraction": 0.5 },
            { "type": "text", "id": "byhand",       "rect": [500, 400, 50, 50], "text": "x" } ] }
        """);

    [Fact]
    public void A_Widget_Is_One_Target_With_The_Union_Of_Its_Components()
    {
        var targets = Targets.All(Layout());
        Assert.Equal(["clock-1", "dial-1", "byhand"], targets.Select(t => t.Id));

        var clock = targets[0];
        Assert.True(clock.IsWidget);
        Assert.Equal(new Rect(100, 100, 80, 60), clock.Bounds);
        Assert.Equal(["clock-1.time", "clock-1.date"], clock.ComponentIds);
    }

    [Fact]
    public void A_Component_Belonging_To_No_Widget_Is_A_Target_On_Its_Own()
    {
        var loose = Targets.All(Layout()).Single(t => t.Id == "byhand");
        Assert.False(loose.IsWidget);
        Assert.Equal(["byhand"], loose.ComponentIds);
        Assert.Equal(new Rect(500, 400, 50, 50), loose.Bounds);
    }

    /// <summary>Nothing with no area: it cannot be clicked and an outline round it says nothing.</summary>
    [Fact]
    public void Zero_Sized_Things_Are_Not_Targets()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "text", "id": "ghost", "rect": [10, 10, 0, 0], "text": "x" } ] }
            """);
        Assert.Empty(Targets.All(layout));
    }

    [Fact]
    public void From_Finds_The_Whole_Widget_A_Selected_Component_Belongs_To()
    {
        var targets = Targets.From(Layout(), ["clock-1.date", "byhand"]);
        Assert.Equal(["clock-1", "byhand"], targets.Select(t => t.Id));
        Assert.Equal(["clock-1.time", "clock-1.date"], targets[0].ComponentIds);
    }

    [Fact]
    public void From_Nothing_Is_Nothing()
        => Assert.Empty(Targets.From(Layout(), []));

    [Fact]
    public void Hit_Finds_What_Is_Under_The_Pointer_And_Nothing_Where_There_Is_Nothing()
    {
        var targets = Targets.All(Layout());
        Assert.Equal("clock-1", Targets.Hit(targets, 150, 150)?.Id);
        Assert.Equal("byhand", Targets.Hit(targets, 500, 400)?.Id);   // the top-left corner counts
        Assert.Null(Targets.Hit(targets, 550, 450));                  // the bottom-right does not
        Assert.Null(Targets.Hit(targets, 900, 900));
    }

    /// <summary>The band catches whatever it touches, not only what it swallows: at fit-to-window
    /// a band that has to contain a widget whole is a band that keeps missing.</summary>
    [Fact]
    public void Within_Catches_Everything_The_Band_Touches()
    {
        var targets = Targets.All(Layout());
        Assert.Equal(["clock-1", "dial-1"], Targets.Within(targets, new Rect(170, 90, 140, 30)).Select(t => t.Id));
        Assert.Empty(Targets.Within(targets, new Rect(0, 0, 10, 10)));
    }

    [Fact]
    public void ComponentIds_Flattens_The_Targets_Without_Repeating_Anything()
    {
        var targets = Targets.All(Layout());
        Assert.Equal(["clock-1.time", "clock-1.date", "dial-1.bar", "byhand"], Targets.ComponentIds(targets));
        Assert.Equal(["clock-1.time", "clock-1.date"], Targets.ComponentIds([targets[0], targets[0]]));
    }
}
