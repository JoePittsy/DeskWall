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

    /// <summary>v2: one target per copy, from the model's expansion, and a copy whose widget is gone
    /// is still a 172x40 box at its origin rather than nothing (plan D1, brief 5). The shipped dial
    /// is used so no other test's user-dir widget can stand in for it.</summary>
    [Fact]
    public void A_Copy_Is_One_Target_And_A_Broken_Copy_Is_Still_A_Box()
    {
        var model = new DesignerModel(LayoutFile.Parse("""
            { "version": 2, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "text", "id": "byhand", "rect": [500, 400, 50, 50], "text": "x" } ],
              "copies": [ { "id": "dial-1", "widget": "dial", "x": 3312, "y": 400 },
                          { "id": "gone-1", "widget": "no-such-widget", "x": 100, "y": 200 } ] }
            """), new Core.Display.DisplaySignature("TEST", 3440, 1440, 100), null);

        var targets = Targets.All(model);

        Assert.Equal(["byhand", "dial-1", "gone-1"], targets.Select(t => t.Id));
        var dial = targets[1];
        Assert.True(dial.IsWidget);
        Assert.All(dial.ComponentIds, id => Assert.StartsWith("dial-1.", id));
        Assert.Equal((3312, 400), (dial.Bounds.X, dial.Bounds.Y));
        Assert.Equal(new Rect(100, 200, 172, 40), targets[2].Bounds);
        Assert.Empty(targets[2].ComponentIds);
        Assert.Equal(["dial-1"], Targets.From(model, [dial.ComponentIds[0]]).Select(t => t.Id));
    }
}
