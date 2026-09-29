using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using Xunit;
using DeskWall.Core.Widgets;

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

    private static Rect Bounds(LayoutFile layout, IReadOnlyList<WidgetTemplate> catalog, string id)
    {
        WidgetTemplate? Find(string key) => catalog.FirstOrDefault(t => t.Key == key);
        return Copies.Bounds(Copies.Find(layout, id)!, WidgetExpander.Expand(layout, Find), Find);
    }

    [Fact]
    public void Top_Anchored_Widgets_Stack_Downwards_With_The_Gap()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var a = TopTemplate("a", 78);
        var b = TopTemplate("b", 40);
        var idA = Copies.Add(layout, a, 0, 999);   // placed anywhere; Arrange repositions
        var idB = Copies.Add(layout, b, 0, 999);

        Arranger.Arrange(layout, [a, b], [idA, idB]);

        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.TopY, 172, 78), Bounds(layout, [a, b], idA));
        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.TopY + 78 + Arranger.Gap, 172, 40), Bounds(layout, [a, b], idB));
    }

    [Fact]
    public void Bottom_Anchored_Widgets_Stack_Upwards_With_The_Gap()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var c = BottomTemplate("c", 92);
        var d = BottomTemplate("d", 20);
        var idC = Copies.Add(layout, c, 0, 0);
        var idD = Copies.Add(layout, d, 0, 0);

        Arranger.Arrange(layout, [c, d], [idC, idD]);

        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.BottomY - 92, 172, 92), Bounds(layout, [c, d], idC));
        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.BottomY - 92 - Arranger.Gap - 20, 172, 20), Bounds(layout, [c, d], idD));
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
        var id = Copies.Add(layout, dial, 0, 0);
        Arranger.Arrange(layout, [dial], [id]);
        Assert.Equal(Arranger.ColumnX, Bounds(layout, [dial], id).X);
    }

    /// <summary>Arrange moves the copy's origin, so a widget whose parts do not start at (0, 0) still
    /// lands with its parts, not its origin, on the column.</summary>
    [Fact]
    public void A_Widget_With_Inset_Parts_Is_Placed_By_Its_Parts()
    {
        var layout = new LayoutFile { BaseImage = "x" };
        var inset = new WidgetTemplate
        {
            Name = "inset", Key = "inset", Description = "d", Width = 172, Height = 50, Anchor = "top",
            Components = [new TextDef { Id = "t", Rect = new Rect(10, 5, 150, 40), Text = PropertyValue.Literal("x") }],
        };
        var id = Copies.Add(layout, inset, 0, 0);
        Arranger.Arrange(layout, [inset], [id]);
        Assert.Equal(new Rect(Arranger.ColumnX, Arranger.TopY, 150, 40), Bounds(layout, [inset], id));
        Assert.Equal((Arranger.ColumnX - 10, Arranger.TopY - 5), (layout.Copies![0].X, layout.Copies[0].Y));
    }
}
