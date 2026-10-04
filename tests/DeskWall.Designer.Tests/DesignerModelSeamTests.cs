using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>The Task 2.2 seam: the expansion follows edits and undo, and depth is not an edit.
/// Uses "dial" because ShippedEditingTests leaves a user-dir clock.json (with no parts) under the
/// run's test DESKWALL_HOME.</summary>
public class DesignerModelSeamTests
{
    private static DesignerModel Model() => new(LayoutFile.Parse("""
        { "version": 2, "baseImage": "x.jpg", "sources": [], "components": [],
          "copies": [ { "id": "dial-1", "widget": "dial", "x": 100, "y": 50 } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    [Fact]
    public void Expanded_Draws_The_Copy_And_Follows_Edit_And_Undo()
    {
        var m = Model();
        var parts = m.Expanded().Layout.Components;
        Assert.NotEmpty(parts);
        Assert.All(parts, c => Assert.Equal("dial-1", c.Widget));
        Assert.Same(m.Expanded(), m.Expanded());   // cached between changes

        var x = parts[0].Rect.X;
        m.Edit("Move copy", l => l.Copies![0].X += 10);
        Assert.Equal(x + 10, m.Expanded().Layout.Components[0].Rect.X);
        m.Undo();
        Assert.Equal(x, m.Expanded().Layout.Components[0].Rect.X);
    }

    [Fact]
    public void A_Missing_Widget_Is_A_Problem_Not_An_Exception()
    {
        var m = Model();
        m.Edit("Break link", l => l.Copies![0].Widget = "no-such-widget");
        Assert.Contains(m.Expanded().Problems, p => p.Kind == ExpandProblemKind.MissingWidget);
    }

    [Fact]
    public void SetDepth_Raises_Once_And_Is_Not_An_Undo_Entry()
    {
        var m = Model(); var n = 0; m.DepthChanged += () => n++;
        Assert.Equal(Depth.Layout, m.Depth);
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        Assert.Equal(1, n);
        Assert.Equal(DepthKind.Copy, m.Depth.Kind);
        Assert.False(m.CanUndo);
        Assert.Empty(m.WidgetEdits);
    }
}
