using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 2.6, the migration gate: the owner's usual job on a v2 layout at layout depth.
/// Select a placed copy, move, nudge, resize and delete it, turn its knobs and open Details on one
/// of its parts, each one undo entry, as on a v1 stamped instance. Uses "dial", never "clock" (see
/// DesignerModelDepthTests).</summary>
public class CopyEditingTests
{
    /// <summary>dial-1 at (100, 50): parts dial (0,0,80,80), value (0,26,80,28), label (0,62,80,16);
    /// gone-1 links to a widget that does not exist; byhand is a loose component.</summary>
    private static DesignerModel Model(string dial1 = "") => new(LayoutFile.Parse($$"""
        { "version": 2, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "text", "id": "byhand", "rect": [600, 600, 50, 20], "text": "x" } ],
          "copies": [
            { "id": "dial-1", "widget": "dial", "x": 100, "y": 50 {{dial1}} },
            { "id": "gone-1", "widget": "no-such-widget", "x": 500, "y": 300 } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    private static WidgetCopy Copy(DesignerModel m, string id) => m.Layout.Copies!.Single(c => c.Id == id);

    private static ComponentDef Part(DesignerModel m, string id) => m.Expanded().Layout.Components.Single(c => c.Id == id);

    private static Target TargetOf(DesignerModel m, string id) => Targets.All(m).Single(t => t.Id == id);

    [Fact]
    public void A_Click_On_Any_Part_Selects_The_Copy_And_A_Broken_Copy_Is_Selectable_Too()
    {
        var m = Model();
        var hit = Targets.Hit(Targets.All(m), 140, 120);   // the label, inside dial-1
        Assert.Equal("dial-1", hit!.Id);
        Assert.Equal(["dial-1"], Targets.EditIds(m.Layout, hit));
        m.Select(Targets.EditIds(m.Layout, hit));
        Assert.Equal(["dial-1"], m.Selection);
        Assert.Equal("dial-1", Assert.Single(Targets.From(m, m.Selection)).Id);

        var broken = TargetOf(m, "gone-1");
        Assert.Empty(broken.ComponentIds);
        Assert.Equal(new Rect(500, 300, 172, 40), broken.Bounds);
        m.Select(["dial-1", .. Targets.EditIds(m.Layout, broken), "byhand"]);
        Assert.Equal(["dial-1", "gone-1", "byhand"], m.Selection);
        Assert.Equal(["byhand", "dial-1", "gone-1"], Targets.From(m, m.Selection).Select(t => t.Id).Order(StringComparer.Ordinal));

        m.Select(["nothing"]);
        Assert.Empty(m.Selection);
    }

    [Fact]
    public void Moving_A_Copy_Moves_Its_Origin_Only_And_Undo_And_Redo_Are_One_Step_Each()
    {
        var m = Model(""", "overrides": { "components.label.rect": "5,62,80,16" }""");
        var before = m.ToJson();
        m.MoveGroups("Move", [(["dial-1"], 10, 7)]);
        Assert.Equal((110, 57), (Copy(m, "dial-1").X, Copy(m, "dial-1").Y));
        Assert.Equal("5,62,80,16", Assert.Single(Copy(m, "dial-1").Overrides).Value.LiteralText);   // untouched
        Assert.Equal(new Rect(115, 119, 80, 16), Part(m, "dial-1.label").Rect);

        m.Undo();
        Assert.Equal(before, m.ToJson());
        Assert.False(m.CanUndo);
        m.Redo();
        Assert.Equal(110, Copy(m, "dial-1").X);
    }

    [Fact]
    public void A_Mixed_Selection_Moves_Together_As_One_Entry_Broken_Copy_Included()
    {
        var m = Model();
        m.Select(["dial-1", "gone-1", "byhand"]);
        var moves = Targets.From(m, m.Selection).Select(t => (Targets.EditIds(m.Layout, t), 1, 0)).ToList();
        m.MoveGroups("Nudge", moves);
        Assert.Equal(101, Copy(m, "dial-1").X);
        Assert.Equal(501, Copy(m, "gone-1").X);
        Assert.Equal(601, m.Layout.Components.Single(c => c.Id == "byhand").Rect.X);
        Assert.Empty(Copy(m, "dial-1").Overrides);
        Assert.DoesNotContain(m.Layout.Components, c => c.Id.StartsWith("dial-1.", StringComparison.Ordinal));   // never flattened
        Assert.Equal(["dial-1", "gone-1", "byhand"], m.Selection);

        m.Undo();
        Assert.Equal(100, Copy(m, "dial-1").X);
        Assert.Equal(500, Copy(m, "gone-1").X);
        Assert.Equal(600, m.Layout.Components.Single(c => c.Id == "byhand").Rect.X);
        Assert.False(m.CanUndo);
    }

    [Fact]
    public void Passing_A_Copy_And_Its_Parts_Moves_It_Once()
    {
        var m = Model();
        m.Move(["dial-1", "dial-1.dial", "dial-1.label"], 3, 0);
        Assert.Equal(103, Copy(m, "dial-1").X);
        Assert.Empty(Copy(m, "dial-1").Overrides);
    }

    [Fact]
    public void Scaling_A_Copy_Scales_Its_Parts_As_Rect_And_Size_Overrides_And_Keeps_The_Rest()
    {
        var m = Model(""", "overrides": { "components.gone.color": "#FF00FF00", "sources.hardware.every": "10" }""");
        var from = TargetOf(m, "dial-1").Bounds;
        Assert.Equal(new Rect(100, 50, 80, 80), from);

        m.Scale("Scale", ["dial-1"], from, new Rect(100, 50, 160, 160), scaleSizes: true);
        var copy = Copy(m, "dial-1");
        Assert.Equal((100, 50), (copy.X, copy.Y));   // the origin stays; the parts scale about it
        Assert.Equal("0,0,160,160", copy.Overrides["components.dial.rect"].LiteralText);
        Assert.Equal("0,52,160,56", copy.Overrides["components.value.rect"].LiteralText);
        Assert.Equal("0,124,160,32", copy.Overrides["components.label.rect"].LiteralText);
        Assert.Equal("36", copy.Overrides["components.value.size"].LiteralText);
        Assert.Equal("#FF00FF00", copy.Overrides["components.gone.color"].LiteralText);   // orphan kept
        Assert.Equal("10", copy.Overrides["sources.hardware.every"].LiteralText);        // source override kept
        Assert.Equal(new Rect(100, 174, 160, 32), Part(m, "dial-1.label").Rect);
        Assert.Equal(new Rect(100, 50, 160, 160), TargetOf(m, "dial-1").Bounds);

        m.Undo();
        Assert.Equal(2, Copy(m, "dial-1").Overrides.Count);
        Assert.False(m.CanUndo);
    }

    [Fact]
    public void An_Edge_Resize_Of_A_Copy_Changes_Rects_Not_Sizes_And_SetRect_Does_The_Same()
    {
        var m = Model();
        m.SetRect("dial-1", new Rect(100, 50, 160, 80));
        var o = Copy(m, "dial-1").Overrides;
        Assert.Equal("0,62,160,16", o["components.label.rect"].LiteralText);
        Assert.False(o.ContainsKey("components.value.size"));
        Assert.Equal(new Rect(100, 50, 160, 80), TargetOf(m, "dial-1").Bounds);
    }

    [Fact]
    public void A_Broken_Copy_Is_Moved_By_A_Scale_Of_Its_Box_And_Removed_By_Delete()
    {
        var m = Model();
        var box = TargetOf(m, "gone-1").Bounds;
        m.Scale("Scale", ["gone-1"], box, new Rect(490, 290, 182, 50), scaleSizes: true);   // top-left grip
        Assert.Equal((490, 290), (Copy(m, "gone-1").X, Copy(m, "gone-1").Y));
        Assert.Empty(Copy(m, "gone-1").Overrides);

        m.Select(["gone-1"]);
        m.Remove(["gone-1"]);
        Assert.DoesNotContain(m.Layout.Copies!, c => c.Id == "gone-1");
        Assert.Empty(m.Selection);
        m.Undo();
        Assert.Contains(m.Layout.Copies!, c => c.Id == "gone-1");
    }

    [Fact]
    public void Deleting_A_Copy_Removes_It_And_Its_Parts_And_Leaves_The_Loose_Component()
    {
        var m = Model();
        m.Select(["dial-1", "byhand"]);
        m.Remove(["dial-1"]);
        Assert.Equal(["gone-1"], m.Layout.Copies!.Select(c => c.Id));
        Assert.DoesNotContain(m.Expanded().Layout.Components, c => c.Widget == "dial-1");
        Assert.Equal(["byhand"], m.Layout.Components.Select(c => c.Id));
        Assert.Equal(["byhand"], m.Selection);

        m.Undo();
        Assert.Equal(3, m.Expanded().Layout.Components.Count(c => c.Widget == "dial-1"));
    }

    [Fact]
    public void A_Knob_Turned_On_The_Selected_Copy_Is_Stored_On_It_And_Undone()
    {
        var m = Model();
        m.Select(["dial-1"]);
        var dial = m.Finder()("dial")!;
        var gpu = dial.Knobs.Single(k => k.Id == "metric").Choices!.Single(c => c.StartsWith("GPU||", StringComparison.Ordinal));
        // What the properties panel's knob rows do (PropertiesPanel.CommitKnob).
        m.Edit("Set Metric", l => Copies.SetKnob(l, dial, "dial-1", "metric", gpu));
        Assert.Equal(gpu, Copy(m, "dial-1").Knobs["metric"]);
        Assert.Equal("gpu", ((TextDef)Part(m, "dial-1.label")).Text.LiteralText);
        Assert.Equal(["dial-1"], m.Selection);

        m.MoveGroups("Move", [(["dial-1"], 0, 5)]);   // the knob survives a move
        Assert.Equal(gpu, Copy(m, "dial-1").Knobs["metric"]);
        Assert.Empty(Copy(m, "dial-1").Overrides);

        m.Undo();
        m.Undo();
        Assert.Empty(Copy(m, "dial-1").Knobs);
        Assert.Equal("cpu", ((TextDef)Part(m, "dial-1.label")).Text.LiteralText);
    }

    [Fact]
    public void Details_Edits_A_Part_At_Copy_Depth_As_An_Override_On_That_Copy()
    {
        var m = Model();
        // What the properties panel's Edit parts does, then what its EditCurrent does.
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        m.Select(["dial-1.label"]);
        Assert.Equal(["dial-1.label"], m.Selection);
        Assert.NotNull(m.Parts.Components.SingleOrDefault(c => c.Id == "dial-1.label"));
        m.EditAtDepth("Set color", l => ((TextDef)l.Components.Single(c => c.Id == "dial-1.label")).Color = PropertyValue.Literal("#FFFF0000"));
        Assert.Equal("#FFFF0000", Assert.Single(Copy(m, "dial-1").Overrides, o => o.Key == "components.label.color").Value.LiteralText);
        Assert.Empty(m.WidgetEdits);

        m.Undo();
        Assert.Empty(Copy(m, "dial-1").Overrides);
    }

    [Fact]
    public void A_Part_Edited_At_Layout_Depth_Is_Also_An_Override_Never_A_Flattened_Component()
    {
        var m = Model();
        m.Select(["dial-1.label"]);   // Details' selection, after a canvas press climbed out of copy depth
        Assert.Equal(["dial-1.label"], m.Selection);
        m.SetRect("dial-1.label", new Rect(100, 120, 80, 16));
        Assert.Equal("0,70,80,16", Copy(m, "dial-1").Overrides["components.label.rect"].LiteralText);
        Assert.DoesNotContain(m.Layout.Components, c => c.Id == "dial-1.label");
    }

    [Fact]
    public void Saving_A_Widget_File_Redraws_Its_Copies_Once_The_Model_Is_Told()
    {
        const string key = "t26-dial";
        var path = Path.Combine(WidgetCatalog.UserDir, key + ".json");
        var shipped = File.ReadAllText(Path.Combine(WidgetCatalog.ShippedDir, "dial.json"));
        Directory.CreateDirectory(WidgetCatalog.UserDir);
        try
        {
            File.WriteAllText(path, shipped);
            var m = new DesignerModel(LayoutFile.Parse($$"""
                { "version": 2, "baseImage": "x.jpg", "sources": [], "components": [],
                  "copies": [ { "id": "{{key}}-1", "widget": "{{key}}", "x": 10, "y": 10 } ] }
                """), new DisplaySignature("T", 1000, 800, 100), null);
            Assert.Equal("#A0FFFFFF", ((TextDef)Part(m, key + "-1.label")).Color.LiteralText);

            File.WriteAllText(path, shipped.Replace("#A0FFFFFF", "#FFFF0000", StringComparison.Ordinal));
            var changed = 0;
            m.Changed += () => changed++;
            m.WidgetsChanged();
            Assert.Equal(1, changed);
            Assert.Equal("#FFFF0000", ((TextDef)Part(m, key + "-1.label")).Color.LiteralText);
            Assert.False(m.CanUndo);
            Assert.False(m.Dirty);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
