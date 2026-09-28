using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
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
    public void Z_Order()
    {
        var m = Model();
        m.BringToFront("clock");
        Assert.True(m.Find("clock")!.Z > m.Find("bar")!.Z);
        m.SendToBack("clock");
        Assert.True(m.Find("clock")!.Z < m.Find("bar")!.Z);
    }

    /// <summary>The align/distribute/drag/nudge path. The offsets are Placement's answer (tested
    /// on their own in PlacementTests); this is the half that writes them, and the thing worth
    /// asserting here is that however many groups move, it is ONE undo entry.</summary>
    [Fact]
    public void MoveGroups_Writes_Every_Group_As_One_Undo_Entry()
    {
        var m = Model();
        m.MoveGroups("Align right", [(["clock"], 10, 0), (["bar"], -4, 6)]);
        Assert.Equal(new Rect(110, 100, 200, 50), m.Find("clock")!.Rect);
        Assert.Equal(new Rect(96, 206, 200, 6), m.Find("bar")!.Rect);

        m.Undo();
        Assert.Equal(new Rect(100, 100, 200, 50), m.Find("clock")!.Rect);
        Assert.Equal(new Rect(100, 200, 200, 6), m.Find("bar")!.Rect);
        Assert.False(m.CanUndo);
    }

    [Fact]
    public void MoveGroups_With_Nothing_To_Do_Is_Not_An_Edit()
    {
        var m = Model();
        m.MoveGroups("Nudge", [(["clock"], 0, 0), (["bar"], 0, 0)]);
        Assert.False(m.CanUndo);
        Assert.False(m.Dirty);
    }

    /// <summary>A resize gesture, however many components and mouse moves it took, is one entry.
    /// The maths itself is ResizeTests' job.</summary>
    [Fact]
    public void Scale_Moves_Everything_In_The_Box_As_One_Undo_Entry()
    {
        var m = Model();
        var from = new Rect(100, 100, 200, 106);        // the two components' bounding box
        m.Scale("Scale", ["clock", "bar"], from, new Rect(100, 100, 400, 212), scaleSizes: true);

        Assert.Equal(new Rect(100, 100, 400, 100), m.Find("clock")!.Rect);
        Assert.Equal(new Rect(100, 300, 400, 12), m.Find("bar")!.Rect);
        // 16 is TextDef's default size, doubled by the doubled height.
        Assert.Equal("32", ((TextDef)m.Find("clock")!).Size.LiteralText);

        m.Undo();
        Assert.Equal(new Rect(100, 100, 200, 50), m.Find("clock")!.Rect);
        Assert.False(m.CanUndo);
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

    [Fact]
    public void Duplicate_Clones_With_An_Offset_As_One_Undo_Entry()
    {
        var m = Model();
        var made = m.Duplicate(["clock", "bar"], 16, 16);
        Assert.Equal(["clock-2", "bar-2"], made);
        Assert.Equal(new Rect(116, 116, 200, 50), m.Find("clock-2")!.Rect);
        Assert.Equal(new Rect(100, 100, 200, 50), m.Find("clock")!.Rect);   // the original is untouched
        m.Undo();
        Assert.Null(m.Find("clock-2"));
        Assert.Null(m.Find("bar-2"));
    }

    private static DesignerModel RepeaterModel() => new(LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "repeater", "id": "drives", "rect": [0, 0, 400, 300], "z": 1,
              "items": { "bind": "disks.drives" },
              "template": [
                { "type": "text", "id": "letter", "rect": [0, 0, 40, 20], "text": "original" } ] } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    [Fact]
    public void Duplicating_A_Repeater_Renames_Its_Template_Children_So_An_Edit_Misses_The_Original()
    {
        var m = RepeaterModel();
        var copyId = Assert.Single(m.Duplicate(["drives"], 16, 16));
        var copy = Assert.IsType<RepeaterDef>(m.Find(copyId));
        var childId = copy.Template[0].Id;
        Assert.NotEqual("letter", childId);

        // Exactly what PropertiesPanel.EditCurrent does: resolve by (parent id, child id) and mutate.
        m.Edit("Set Text", l =>
            ((TextDef)ComponentLookup.Find(l, copyId, childId)!.Value.Def).Text = PropertyValue.Literal("copy"));

        Assert.Equal("copy", ChildText(m, copyId, childId));
        Assert.Equal("original", ChildText(m, "drives", "letter"));
    }

    [Fact]
    public void Find_Resolves_A_Template_Child_Under_The_Named_Parent_Not_The_First_Match()
    {
        // The collision Duplicate used to make, and that a hand-written layout can still make.
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [
                { "type": "repeater", "id": "a", "rect": [0, 0, 100, 100], "items": { "bind": "d.x" },
                  "template": [ { "type": "text", "id": "letter", "rect": [0, 0, 10, 10], "text": "a" } ] },
                { "type": "repeater", "id": "b", "rect": [0, 0, 100, 100], "items": { "bind": "d.x" },
                  "template": [ { "type": "text", "id": "letter", "rect": [0, 0, 10, 10], "text": "b" } ] } ] }
            """);

        var found = ComponentLookup.Find(layout, "b", "letter");
        Assert.NotNull(found);
        Assert.Equal("b", found!.Value.Parent!.Id);
        Assert.Equal("b", ((TextDef)found.Value.Def).Text.LiteralText);
        Assert.Null(ComponentLookup.Find(layout, "a", "nope"));
    }

    private static string? ChildText(DesignerModel m, string parentId, string childId)
        => ((TextDef)ComponentLookup.Find(m.Layout, parentId, childId)!.Value.Def).Text.LiteralText;
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

    [Fact]
    public void Lines_Are_The_Canvas_Edges_And_Each_Others_Edges_And_Centre()
    {
        var (xs, ys) = Snap.Lines([new Rect(100, 200, 60, 40)], new Rect(0, 0, 1000, 800));
        Assert.Equal([0, 1000, 100, 160, 130], xs);
        Assert.Equal([0, 800, 200, 240, 220], ys);
    }

    [Fact]
    public void Edge_Pulls_One_Value_To_The_Nearest_Candidate_In_Range()
    {
        Assert.Equal(160, Snap.Edge(157, [100, 160, 400], Snap.Threshold, out var near));
        Assert.Equal(160, near);
        Assert.Equal(104, Snap.Edge(103, [100, 104], Snap.Threshold, out var nearest));
        Assert.Equal(104, nearest);
        Assert.Equal(300, Snap.Edge(300, [100, 160, 400], Snap.Threshold, out var far));
        Assert.Null(far);
    }
}
