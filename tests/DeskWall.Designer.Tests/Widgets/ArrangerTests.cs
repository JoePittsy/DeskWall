using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

public class ArrangerTests
{
    private static WidgetTemplate TopTemplate(string key, int height) => new()
    {
        Name = key, Key = key, Description = "d", Width = 172, Height = height, Anchor = "top",
        Components = [new TextDef { Id = "t", Rect = new Rect(0, 0, 172, height), Text = PropertyValue.Literal("x") }],
    };

    private static WidgetTemplate BottomTemplate(string key, int height) => new()
    {
        Name = key, Key = key, Description = "d", Width = 172, Height = height, Anchor = "bottom",
        Components = [new TextDef { Id = "t", Rect = new Rect(0, 0, 172, height), Text = PropertyValue.Literal("x") }],
    };

    [Fact]
    public void Top_Anchored_Widgets_Stack_Downwards_With_The_Gap()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var a = TopTemplate("a", 78);
        var b = TopTemplate("b", 40);
        var idA = WidgetInstance.Add(layout, a, new Rect(0, 999, 0, 0));   // placed anywhere; Arrange repositions
        var idB = WidgetInstance.Add(layout, b, new Rect(0, 999, 0, 0));

        Arranger.Arrange(layout, [a, b], [idA, idB]);

        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.TopY, 172, 78), WidgetInstance.Bounds(layout, idA));
        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.TopY + 78 + Arranger.Gap, 172, 40), WidgetInstance.Bounds(layout, idB));
    }

    [Fact]
    public void Bottom_Anchored_Widgets_Stack_Upwards_With_The_Gap()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var c = BottomTemplate("c", 92);
        var d = BottomTemplate("d", 20);
        var idC = WidgetInstance.Add(layout, c, new Rect(0, 0, 0, 0));
        var idD = WidgetInstance.Add(layout, d, new Rect(0, 0, 0, 0));

        Arranger.Arrange(layout, [c, d], [idC, idD]);

        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.BottomY - 92, 172, 92), WidgetInstance.Bounds(layout, idC));
        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.BottomY - 92 - Arranger.Gap - 20, 172, 20), WidgetInstance.Bounds(layout, idD));
    }

    [Fact]
    public void A_Narrow_Widget_Sits_At_ColumnX_Not_Centred()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var dial = new WidgetTemplate
        {
            Name = "dial", Key = "dial", Description = "d", Width = 80, Height = 80, Anchor = "top",
            Components = [new TextDef { Id = "t", Rect = new Rect(0, 0, 80, 80), Text = PropertyValue.Literal("x") }],
        };
        var id = WidgetInstance.Add(layout, dial, new Rect(0, 0, 0, 0));
        Arranger.Arrange(layout, [dial], [id]);
        Assert.Equal(Arranger.ColumnX, WidgetInstance.Bounds(layout, id).X);
    }

    [Fact]
    public void Unlocked_Instances_Are_Skipped_And_Keep_Their_Rect()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var a = TopTemplate("a", 78);
        var idA = WidgetInstance.Add(layout, a, new Rect(500, 500, 0, 0));
        layout.Widgets![idA].Unlocked = true;

        Arranger.Arrange(layout, [a], [idA]);

        Assert.Equal(new Rect(500, 500, 172, 78), WidgetInstance.Bounds(layout, idA));
    }

    [Fact]
    public void Order_Sorts_By_Y_With_Unlocked_Last()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var a = TopTemplate("a", 78);
        var b = TopTemplate("b", 40);
        var idA = WidgetInstance.Add(layout, a, new Rect(0, 500, 0, 0));
        var idB = WidgetInstance.Add(layout, b, new Rect(0, 100, 0, 0));
        layout.Widgets![idA].Unlocked = true;   // "a" is lower on screen but unlocked

        var order = Arranger.Order(layout);

        Assert.Equal([idB, idA], order);
    }
}
