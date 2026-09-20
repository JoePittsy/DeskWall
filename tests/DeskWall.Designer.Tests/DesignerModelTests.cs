using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using Xunit;

public class DesignerModelTests
{
    private static DesignerModel Model() => new(LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "text", "id": "clock", "rect": [100, 100, 200, 50], "z": 1, "text": "x" },
            { "type": "bar", "id": "bar", "rect": [100, 200, 200, 6], "z": 2, "fraction": 0.5 } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    [Fact]
    public void Move_Is_Undoable_And_Notifies()
    {
        var m = Model(); var n = 0; m.Changed += () => n++;
        m.Move(["clock"], 10, -5);
        Assert.Equal(new Rect(110, 95, 200, 50), m.Find("clock")!.Rect);
        Assert.True(m.Dirty); Assert.True(m.CanUndo); Assert.Equal(1, n);
        m.Undo();
        Assert.Equal(new Rect(100, 100, 200, 50), m.Find("clock")!.Rect);
        Assert.True(m.CanRedo); Assert.Equal(2, n);
        Assert.False(m.Dirty);
        m.Redo();
        Assert.Equal(new Rect(110, 95, 200, 50), m.Find("clock")!.Rect);
    }

    [Fact]
    public void Add_Makes_Ids_Unique_And_Remove_Clears_Selection()
    {
        var m = Model();
        m.Add(new TextDef { Id = "clock", Rect = new Rect(0, 0, 10, 10), Text = PropertyValue.Literal("y") });
        Assert.NotNull(m.Find("clock-2"));
        m.Select(["clock-2", "bar"]);
        m.Remove(["clock-2"]);
        Assert.Null(m.Find("clock-2"));
        Assert.Equal(["bar"], m.Selection);
    }

    [Fact]
    public void Z_Order_And_Align()
    {
        var m = Model();
        m.BringToFront("clock");
        Assert.True(m.Find("clock")!.Z > m.Find("bar")!.Z);
        m.SendToBack("clock");
        Assert.True(m.Find("clock")!.Z < m.Find("bar")!.Z);
        m.Align(["clock", "bar"], AlignEdge.Right);
        Assert.Equal(m.Find("clock")!.Rect.Right, m.Find("bar")!.Rect.Right);
    }

    [Fact]
    public void Undo_Is_Capped_At_100()
    {
        var m = Model();
        for (var i = 0; i < 150; i++) m.Move(["clock"], 1, 0);
        var undone = 0; while (m.CanUndo) { m.Undo(); undone++; }
        Assert.Equal(100, undone);
        Assert.Equal(150, m.Find("clock")!.Rect.X);   // 100 + the 50 moves that fell off the stack
    }

    [Fact]
    public void Save_And_Revert()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "designer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var m = Model(); m.Path = Path.Combine(dir, "l.json");
        m.Save();
        Assert.False(m.Dirty);
        m.Move(["clock"], 5, 5);
        Assert.True(m.Dirty);
        m.RevertToSaved();
        Assert.False(m.Dirty);
        Assert.Equal(new Rect(100, 100, 200, 50), m.Find("clock")!.Rect);
        Assert.Equal(new Rect(100, 100, 200, 50), LayoutFile.Load(m.Path).Components[0].Rect);
    }
}

public class SnapTests
{
    [Fact]
    public void Snaps_To_Neighbour_Edges_And_Canvas_Within_Threshold()
    {
        var canvas = new Rect(0, 0, 1000, 800);
        var other = new Rect(300, 100, 100, 100);
        var (r, g) = Snap.Apply(new Rect(404, 96, 50, 50), [other], canvas);
        Assert.Equal(400, r.X);                      // left edge to other's right edge
        Assert.Equal(100, r.Y);                      // top to other's top
        Assert.Contains(g, x => x.Vertical && x.Position == 400);
        Assert.Contains(g, x => !x.Vertical && x.Position == 100);
        var (r2, g2) = Snap.Apply(new Rect(3, 3, 50, 50), [], canvas);
        Assert.Equal(new Rect(0, 0, 50, 50), r2);
        Assert.Equal(2, g2.Count);
        var (r3, g3) = Snap.Apply(new Rect(500, 500, 50, 50), [other], canvas);
        Assert.Equal(new Rect(500, 500, 50, 50), r3);
        Assert.Empty(g3);
    }
}
