using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 2.3: one undo stack over the layout and the widget overlay, the fork on Save, and
/// depth pruning. Expands "dial" and "uptime", never "clock" (ShippedEditingTests leaves a part-less
/// user-dir clock.json in the shared test home).</summary>
public class DesignerModelDepthTests
{
    internal static DesignerModel Model(string copies = """{ "id": "dial-1", "widget": "dial", "x": 100, "y": 50 }""") => new(LayoutFile.Parse($$"""
        { "version": 2, "baseImage": "x.jpg", "sources": [], "components": [], "copies": [ {{copies}} ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    /// <summary>The template with its <paramref name="partId"/> part's colour set (a fresh template:
    /// the overlay is never mutated in place).</summary>
    internal static WidgetTemplate Recolour(WidgetTemplate t, string partId, string color)
    {
        var parts = LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = [.. t.Components] }.ToJson()).Components;
        ((TextDef)parts.Single(c => c.Id == partId)).Color = PropertyValue.Literal(color);
        return new WidgetTemplate
        {
            Name = t.Name, Key = t.Key, Description = t.Description, Width = t.Width, Height = t.Height,
            Anchor = t.Anchor, Sources = t.Sources, Components = parts, Knobs = t.Knobs,
        };
    }

    private static string LabelColour(DesignerModel m, string copyId = "dial-1")
        => ((TextDef)m.Expanded().Layout.Components.Single(c => c.Id == copyId + ".label")).Color.LiteralText!;

    [Fact]
    public void An_Undo_Across_A_Widget_Edit_Restores_The_Layout_And_The_Widget_Together()
    {
        var m = Model();
        var dial = m.Finder()("dial")!;
        var colour = LabelColour(m);

        m.Edit("Edit widget", (l, edits) => { l.Copies![0].X += 7; edits["dial"] = Recolour(dial, "label", "#FF123456"); });
        Assert.Equal("#FF123456", LabelColour(m));
        Assert.True(m.Dirty);

        m.Undo();
        Assert.Equal(colour, LabelColour(m));
        Assert.Equal(100, m.Layout.Copies![0].X);
        Assert.Empty(m.WidgetEdits);
        Assert.False(m.Dirty);

        m.Redo();
        Assert.Equal("#FF123456", LabelColour(m));
        Assert.Equal(107, m.Layout.Copies![0].X);
    }

    [Fact]
    public void The_Overlay_Alone_Makes_The_Document_Dirty()
    {
        var m = Model();
        m.Edit("Edit widget", (_, edits) => edits["dial"] = Recolour(m.Finder()("dial")!, "label", "#FF123456"));
        Assert.True(m.Dirty);
    }

    [Fact]
    public void An_Undo_Of_Make_Widget_Removes_Both_The_Copy_And_The_Overlay_Entry()
    {
        var m = Model();
        m.Edit("Add", l => l.Components.Add(new TextDef { Id = "hello", Rect = new Rect(10, 10, 50, 20), Text = PropertyValue.Literal("hi") }));

        var made = Lens.MakeWidget(m, ["hello"]);
        Assert.Contains(m.Layout.Copies!, c => c.Id == made);
        Assert.Single(m.WidgetEdits);

        m.Undo();
        Assert.DoesNotContain(m.Layout.Copies!, c => c.Id == made);
        Assert.Empty(m.WidgetEdits);
        Assert.NotNull(m.Find("hello"));
    }

    [Fact]
    public void Save_Forks_A_Shipped_Key_Into_The_User_Dir_And_Leaves_The_Shipped_Dir_Untouched()
    {
        var shipped = Path.Combine(WidgetCatalog.ShippedDir, "uptime.json");
        var fork = Path.Combine(WidgetCatalog.UserDir, "uptime.json");
        var layoutPath = Path.Combine(Path.GetTempPath(), $"deskwall-depth-{Guid.NewGuid():N}.json");
        var shippedBytes = File.ReadAllBytes(shipped);
        Assert.False(File.Exists(fork));
        try
        {
            var m = Model("""{ "id": "uptime-1", "widget": "uptime", "x": 0, "y": 0 }""");
            m.Path = layoutPath;
            m.Edit("Edit widget", (_, edits) => edits["uptime"] = Recolour(m.Finder()("uptime")!, "uptime", "#FFABCDEF"));
            m.Save();

            Assert.False(m.Dirty);
            Assert.Equal(shippedBytes, File.ReadAllBytes(shipped));
            var forked = WidgetTemplate.Load(fork);
            Assert.Equal("#FFABCDEF", ((TextDef)forked.Components.Single()).Color.LiteralText);
            Assert.Single(LayoutFile.Load(layoutPath).Copies!);
            Assert.False(File.Exists(fork + ".tmp"));

            // An undo past the Apply shows the widget as it was before, not the new fork on disk.
            m.Undo();
            Assert.NotEqual("#FFABCDEF", ((TextDef)m.Expanded().Layout.Components.Single()).Color.LiteralText);
            Assert.True(m.Dirty);
        }
        finally
        {
            File.Delete(fork);
            File.Delete(layoutPath);
        }
    }

    /// <summary>Task 6.1: Apply refuses a widget the daemon could not read back (here, no
    /// description), naming it, and writes nothing at all: no widget, no layout, no temp file.</summary>
    [Fact]
    public void Save_Refuses_A_Widget_That_Would_Not_Load_And_Writes_Nothing()
    {
        var fork = Path.Combine(WidgetCatalog.UserDir, "uptime.json");
        var layoutPath = Path.Combine(Path.GetTempPath(), $"deskwall-depth-{Guid.NewGuid():N}.json");
        try
        {
            var m = Model("""{ "id": "uptime-1", "widget": "uptime", "x": 0, "y": 0 }""");
            m.Path = layoutPath;
            Assert.True(Lens.EditWidget(m, "uptime", "Describe", d => d.Description = ""));

            var ex = Assert.Throws<InvalidOperationException>(m.Save);
            Assert.Contains("Uptime", ex.Message, StringComparison.Ordinal);
            Assert.Contains("description", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(fork));
            Assert.False(File.Exists(fork + ".tmp"));
            Assert.False(File.Exists(layoutPath));
            Assert.True(m.Dirty);
        }
        finally
        {
            File.Delete(fork);
            File.Delete(layoutPath);
        }
    }

    /// <summary>Task 6.1, Reset: once the fork's file is deleted, <see cref="DesignerModel.ForgetWidget"/>
    /// drops its overlay entry without an undo entry and without making the document dirty, and the
    /// copies draw the shipped widget again.</summary>
    [Fact]
    public void ForgetWidget_After_A_Reset_Puts_The_Copies_Back_On_The_Shipped_Widget()
    {
        var fork = Path.Combine(WidgetCatalog.UserDir, "uptime.json");
        var layoutPath = Path.Combine(Path.GetTempPath(), $"deskwall-depth-{Guid.NewGuid():N}.json");
        try
        {
            var m = Model("""{ "id": "uptime-1", "widget": "uptime", "x": 0, "y": 0 }""");
            m.Path = layoutPath;
            string Colour() => ((TextDef)m.Expanded().Layout.Components.Single()).Color.LiteralText!;
            var shipped = Colour();
            m.Edit("Edit widget", (_, edits) => edits["uptime"] = Recolour(m.Finder()("uptime")!, "uptime", "#FFABCDEF"));
            m.Save();
            Assert.Equal("#FFABCDEF", Colour());

            File.Delete(fork);
            var undo = m.CanUndo;
            m.ForgetWidget("uptime");
            Assert.Empty(m.WidgetEdits);
            Assert.False(m.Dirty);
            Assert.Equal(shipped, Colour());
            Assert.Equal(undo, m.CanUndo);
        }
        finally
        {
            File.Delete(fork);
            File.Delete(layoutPath);
        }
    }

    [Fact]
    public void An_Undo_While_At_The_Depth_Of_A_Removed_Copy_Climbs_To_Layout_Depth()
    {
        var m = Model();
        m.Edit("Add copy", l => l.Copies!.Add(new WidgetCopy { Id = "dial-2", Widget = "dial", X = 300, Y = 50 }));
        m.SetDepth(Depth.Copy("dial-2", "dial"));
        var raised = 0; m.DepthChanged += () => raised++;

        m.Undo();
        Assert.Equal(Depth.Layout, m.Depth);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Widget_Depth_Loses_Only_Its_Origin_When_The_Copy_Goes()
    {
        var m = Model();
        m.Edit("Add copy", l => l.Copies!.Add(new WidgetCopy { Id = "dial-2", Widget = "dial", X = 300, Y = 50 }));
        m.SetDepth(Depth.Widget("dial", "dial-2"));
        m.Undo();
        Assert.Equal(Depth.Widget("dial", null), m.Depth);
    }

    [Fact]
    public void RevertToSaved_Restores_The_Copies_And_The_Overlay()
    {
        var m = Model();
        m.Edit("Edit", (l, edits) =>
        {
            l.Copies!.Add(new WidgetCopy { Id = "dial-2", Widget = "dial" });
            edits["dial"] = Recolour(m.Finder()("dial")!, "label", "#FF123456");
        });
        m.RevertToSaved();
        Assert.Equal(["dial-1"], m.Layout.Copies!.Select(c => c.Id));
        Assert.Empty(m.WidgetEdits);
        Assert.False(m.Dirty);
        m.Undo();
        Assert.Equal(2, m.Layout.Copies!.Count);
        Assert.Single(m.WidgetEdits);
    }
}
