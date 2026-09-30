using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using Xunit;

/// <summary>Task 4.4: what the Insert panel puts on the canvas besides a value (parts, widget
/// copies, typed text), which sources its Data section runs, and the brief's "CPU load dial with a
/// label" sequence end to end on the model.</summary>
public class InsertTests
{
    private static readonly SourceDef Hardware = new() { Name = "hardware", Type = "hardware" };
    private static readonly ValueEntry Cpu = new("hardware.cpu", "CPU load", ValueKind.Fraction, new NumberValue(0.27), Hardware);

    private static DesignerModel Empty() => new(LayoutFile.Parse("""
        { "version": 2, "baseImage": "x.jpg", "sources": [], "components": [] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    private static void OneUndoEntry(DesignerModel m, string before)
    {
        m.Undo();
        Assert.False(m.CanUndo);
        Assert.Equal(before, m.ToJson());
    }

    [Fact]
    public void Data_Runs_The_Layouts_Sources_Then_The_Local_Defaults_And_The_Layouts_Win()
    {
        var m = new DesignerModel(LayoutFile.Parse("""
            { "version": 2, "baseImage": "x.jpg", "components": [],
              "sources": [ { "name": "hardware", "type": "hardware", "every": 60 }, { "name": "feed", "type": "rss", "every": 900 } ] }
            """), new DisplaySignature("T", 1000, 800, 100), null);
        var defs = Insert.DataSources(m);
        Assert.Equal(["hardware", "feed", "time", "disks", "system", "audio", "battery", "media"], defs.Select(d => d.Name));
        Assert.Equal(60, defs[0].EverySeconds);
    }

    [Fact]
    public void At_Widget_Depth_Data_Runs_The_Widgets_Sources_First()
    {
        var m = DesignerModelDepthTests.Model();
        m.SetDepth(Depth.Widget("dial", "dial-1"));
        var widget = m.Parts.Sources.Select(s => s.Name).ToList();
        Assert.NotEmpty(widget);
        Assert.Equal(widget, Insert.DataSources(m).Take(widget.Count).Select(d => d.Name));
    }

    [Fact]
    public void A_Part_Lands_Centred_With_A_Unique_Id_As_One_Undo_Entry()
    {
        var m = Empty();
        var before = m.ToJson();
        var id = Insert.Part(m, PartKind.Dial, 500, 300);
        Assert.Equal("dial", id);
        Assert.Equal(new Rect(460, 260, 80, 80), m.Find(id!)!.Rect);
        OneUndoEntry(m, before);

        Insert.Part(m, PartKind.Text, 500, 300);
        Assert.Equal("text-2", Insert.Part(m, PartKind.Text, 500, 400));
        Assert.Equal("center", ((TextDef)m.Find("text-2")!).Align.LiteralText);
        Assert.Equal("none", ((TextDef)m.Find("text-2")!).Effect.LiteralText);   // as the shipped dial's texts (critique 3)
    }

    [Fact]
    public void At_Layout_Depth_With_Copies_A_Part_Is_A_Loose_Component()
    {
        var m = DesignerModelDepthTests.Model();
        var id = Insert.Part(m, PartKind.Bar, 700, 600);
        Assert.Equal("bar", id);
        Assert.Contains(m.Layout.Components, c => c.Id == "bar" && c.Widget is null);
        Assert.Empty(m.Layout.Copies!.Single().Overrides);
    }

    [Fact]
    public void At_Copy_Depth_No_Part_Is_Added_And_At_Widget_Depth_It_Joins_The_Widget()
    {
        var m = DesignerModelDepthTests.Model();
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        Assert.False(Insert.CanAddPart(m));
        Assert.Null(Insert.Part(m, PartKind.Text, 140, 90));
        Assert.False(m.CanUndo);

        m.SetDepth(Depth.Widget("dial", "dial-1"));
        var parts = m.Parts.Components.Count;
        var id = Insert.Part(m, PartKind.Text, 140, 90);
        Assert.Equal("dial-1.text", id);
        Assert.Equal(parts + 1, m.WidgetEdits["dial"].Components.Count);
        Assert.Contains(m.WidgetEdits["dial"].Components, c => c.Id == "text");
    }

    [Fact]
    public void A_Widget_Lands_Centred_At_Layout_Depth_Only()
    {
        var m = DesignerModelDepthTests.Model();
        var dial = m.Finder()("dial")!;
        var before = m.ToJson();
        var id = Insert.Widget(m, dial, 600, 400);
        var copy = m.Layout.Copies!.Single(c => c.Id == id);
        Assert.Equal((600 - dial.Width / 2, 400 - dial.Height / 2), (copy.X, copy.Y));
        OneUndoEntry(m, before);

        m.SetDepth(Depth.Copy("dial-1", "dial"));
        Assert.Null(Insert.Widget(m, dial, 600, 400));
    }

    [Fact]
    public void PartAt_Finds_The_Frontmost_Part_Including_A_Copys()
    {
        var m = DesignerModelDepthTests.Model();
        Assert.StartsWith("dial-1.", Insert.PartAt(m, 140, 90));
        Assert.Null(Insert.PartAt(m, 900, 700));
        var top = m.Parts.Components.Where(c => c.Rect.X <= 140 && 140 < c.Rect.Right && c.Rect.Y <= 90 && 90 < c.Rect.Bottom).MaxBy(c => c.Z)!;
        Assert.Equal(top.Id, Insert.PartAt(m, 140, 90));   // the highest z, as painted
        Insert.Part(m, PartKind.Dial, 700, 500);
        Insert.Part(m, PartKind.Bar, 700, 500);
        Assert.Equal("bar", Insert.PartAt(m, 700, 500));   // the same z: the later one is on top
    }

    [Fact]
    public void Typed_Text_Is_One_Undo_Entry_And_Nothing_When_Unchanged()
    {
        var m = Empty();
        var id = Insert.Part(m, PartKind.Text, 100, 100)!;
        var before = m.ToJson();
        Insert.SetText(m, id, "cpu");
        Assert.Equal("cpu", ((TextDef)m.Find(id)!).Text.LiteralText);
        m.Undo();
        Assert.Equal(before, m.ToJson());
        Insert.SetText(m, id, "Text");
        Assert.Equal(before, m.ToJson());
        Assert.True(m.CanUndo);   // only the add is left
        m.Undo();
        Assert.False(m.CanUndo);
    }

    /// <summary>Brief section 2's sequence, on the model: drop CPU load (Dial), drop it again on the
    /// dial (27%), a Text part below it, type "cpu", select all, Ctrl+Alt+K.</summary>
    [Fact]
    public void The_Brief_Sequence_Makes_A_Cpu_Dial_Widget_With_A_Label()
    {
        var m = Empty();
        var dialPlan = DropPlan.For(m, Cpu, new DropTarget(500, 300));
        var dial = dialPlan.Apply(m, dialPlan.Options[0]);
        var textPlan = DropPlan.For(m, Cpu, new DropTarget(500, 300, Insert.PartAt(m, 500, 300)));
        var value = textPlan.Apply(m, textPlan.Options[0]);
        var label = Insert.Part(m, PartKind.Text, 500, 360)!;
        Insert.SetText(m, label, "cpu");
        m.SelectAll();
        Assert.Equal(3, m.Selection.Count);
        Assert.True(m.MakeOrEditWidget());

        Assert.Equal(DepthKind.Widget, m.Depth.Kind);
        var widget = m.WidgetEdits[m.Depth.WidgetKey!];
        Assert.Equal(3, widget.Components.Count);
        Assert.Equal("hardware", Assert.Single(widget.Sources).Name);
        Assert.Contains(widget.Components, c => c is DialDef);
        Assert.Contains(widget.Components, c => c is TextDef { Text.IsBound: true });
        Assert.Contains(widget.Components, c => c is TextDef t && t.Text.LiteralText == "cpu");
        Assert.Empty(m.Layout.Components);
        Assert.Single(m.Layout.Copies!);
        Assert.NotEqual(dial, value);
    }
}
