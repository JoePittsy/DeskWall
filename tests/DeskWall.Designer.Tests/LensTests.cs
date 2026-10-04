using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 2.4: the model's ordinary component edits, run at copy and widget depth through
/// <see cref="Lens"/>. Uses "dial", never "clock" (see DesignerModelDepthTests).</summary>
public class LensTests
{
    private static DesignerModel TwoDials(string dial1Overrides = "{}") => DesignerModelDepthTests.Model($$"""
        { "id": "dial-1", "widget": "dial", "x": 100, "y": 50, "overrides": {{dial1Overrides}} },
        { "id": "dial-2", "widget": "dial", "x": 300, "y": 50 }
        """);

    private static ComponentDef Part(DesignerModel m, string id) => m.Expanded().Layout.Components.Single(c => c.Id == id);

    private static string Colour(DesignerModel m, string id) => ((TextDef)Part(m, id)).Color.LiteralText!;

    private static WidgetCopy Copy(DesignerModel m, string id) => m.Layout.Copies!.Single(c => c.Id == id);

    [Fact]
    public void Moving_A_Part_At_Copy_Depth_Writes_Its_Rect_Override_And_Nothing_Else()
    {
        var m = TwoDials();
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        Assert.Equal(new Rect(100, 112, 80, 16), m.Find("dial-1.label")!.Rect);   // absolute

        m.Move(["dial-1.label"], 5, 0);
        var o = Assert.Single(Copy(m, "dial-1").Overrides);
        Assert.Equal("components.label.rect", o.Key);
        Assert.Equal("5,62,80,16", o.Value.LiteralText);
        Assert.Equal(new Rect(105, 112, 80, 16), Part(m, "dial-1.label").Rect);
        Assert.Equal(new Rect(300, 112, 80, 16), Part(m, "dial-2.label").Rect);
        Assert.Empty(m.WidgetEdits);

        m.Move(["dial-1.label"], -5, 0);
        Assert.Empty(Copy(m, "dial-1").Overrides);
    }

    [Fact]
    public void Removing_A_Part_At_Copy_Depth_Hides_It_And_Keeps_Its_Other_Overrides()
    {
        var m = TwoDials("""{ "components.label.color": "#FF00FF00" }""");
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        m.Remove(["dial-1.label"]);
        var overrides = Copy(m, "dial-1").Overrides;
        Assert.Equal("true", overrides["components.label.hidden"].LiteralText);
        Assert.Equal("#FF00FF00", overrides["components.label.color"].LiteralText);
        Assert.DoesNotContain(m.Expanded().Layout.Components, c => c.Id == "dial-1.label");

        Lens.Reset(m, "dial-1", "components.label.hidden");
        Assert.Equal("#FF00FF00", Colour(m, "dial-1.label"));
    }

    [Fact]
    public void An_Orphan_Override_Survives_An_Edit_At_Copy_Depth()
    {
        var m = TwoDials("""{ "components.gone.color": "#FF00FF00" }""");
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        m.Move(["dial-1.value"], 0, 3);
        var overrides = Copy(m, "dial-1").Overrides;
        Assert.Equal(2, overrides.Count);
        Assert.True(overrides.ContainsKey("components.gone.color"));
    }

    [Fact]
    public void A_Colour_Change_At_Widget_Depth_Reaches_A_Copy_Without_An_Override_And_Not_One_With()
    {
        var m = TwoDials("""{ "components.label.color": "#FF00FF00" }""");
        m.SetDepth(Depth.Widget("dial", "dial-1"));
        Assert.Equal(new Rect(100, 112, 80, 16), m.Find("dial-1.label")!.Rect);   // at the copy's origin

        m.EditAtDepth("Colour", l => ((TextDef)l.Components.Single(c => c.Id == "dial-1.label")).Color = PropertyValue.Literal("#FF0000FF"));
        Assert.Equal("#FF0000FF", Colour(m, "dial-2.label"));
        Assert.Equal("#FF00FF00", Colour(m, "dial-1.label"));
        var edited = m.WidgetEdits["dial"];
        Assert.Equal(new Rect(0, 62, 80, 16), edited.Components.Single(c => c.Id == "label").Rect);   // minus the origin
        Assert.Equal("#FF00FF00", Copy(m, "dial-1").Overrides["components.label.color"].LiteralText);

        m.Undo();
        Assert.Empty(m.WidgetEdits);
    }

    [Fact]
    public void Push_To_Widget_Empties_The_Override_And_Changes_The_Other_Copy()
    {
        var m = TwoDials("""{ "components.label.color": "#FF00FF00" }""");
        Assert.True(Lens.PushToWidget(m, "dial-1", "components.label.color"));
        Assert.Empty(Copy(m, "dial-1").Overrides);
        Assert.Equal("#FF00FF00", Colour(m, "dial-1.label"));
        Assert.Equal("#FF00FF00", Colour(m, "dial-2.label"));
        Assert.False(Lens.PushToWidget(m, "dial-1", "components.label.color"));

        m.Undo();   // one entry for both halves
        Assert.Single(Copy(m, "dial-1").Overrides);
        Assert.Empty(m.WidgetEdits);
    }

    [Fact]
    public void MakeWidget_Round_Trips_Expanding_Gives_The_Original_Components_Back()
    {
        var m = new DesignerModel(LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg",
              "sources": [ { "name": "hardware", "type": "hardware" }, { "name": "system", "type": "system" } ],
              "components": [
                { "type": "dial", "id": "cpu", "rect": [200, 300, 80, 80], "z": 1, "fraction": { "bind": "hardware.cpu" }, "threshold": 0.9 },
                { "type": "text", "id": "cpu.label", "rect": [210, 390, 60, 16], "z": 2, "text": { "bind": "hardware.cpuPct | \"{0}%\"" }, "size": 11 },
                { "type": "text", "id": "other", "rect": [0, 0, 60, 16], "z": 1, "text": "stays" } ] }
            """), new DisplaySignature("T", 1000, 800, 100), null);
        var originals = Json(m.Layout.Components.Where(c => c.Id != "other"));

        var copyId = Lens.MakeWidget(m, ["cpu", "cpu.label"])!;
        var copy = Copy(m, copyId);
        Assert.Equal((200, 300), (copy.X, copy.Y));
        var template = m.WidgetEdits[copy.Widget];
        Assert.Equal((80, 106), (template.Width, template.Height));
        Assert.Equal(["hardware"], template.Sources.Select(s => s.Name));
        Assert.All(template.Components, c => Assert.Matches("^[A-Za-z_][A-Za-z0-9_-]*$", c.Id));
        Assert.Equal(["other"], m.Layout.Components.Select(c => c.Id));
        Assert.Equal(2, m.Layout.Version);

        var expanded = m.Expanded();
        Assert.Empty(expanded.Problems);
        Assert.Equal(originals, Json(expanded.Layout.Components.Where(c => c.Widget == copyId)));
        Assert.Equal(["hardware", "system"], expanded.Layout.Sources.Select(s => s.Name));   // shared, not renamed

        // It saves and loads back as a widget file.
        var loaded = TemplateRoundTrip(template);
        Assert.Equal(template.Components.Count, loaded.Components.Count);
    }

    [Fact]
    public void A_New_Key_Never_Collides_With_A_User_Widget_Or_Another_New_One()
    {
        var user = Path.Combine(WidgetCatalog.UserDir, "new-widget.json");
        Directory.CreateDirectory(WidgetCatalog.UserDir);
        File.WriteAllText(user, "not even json");   // a broken file still owns its key
        try
        {
            var m = DesignerModelDepthTests.Model();
            var a = Lens.NewWidget(m, 10, 10);
            var b = Lens.NewWidget(m, 20, 20);
            Assert.Equal("new-widget-2", Copy(m, a).Widget);
            Assert.Equal("new-widget-3", Copy(m, b).Widget);
        }
        finally { File.Delete(user); }
    }

    [Fact]
    public void NewWidget_Places_An_Empty_Frame_And_Goes_To_Widget_Depth()
    {
        var m = DesignerModelDepthTests.Model();
        var copyId = Lens.NewWidget(m, 400, 200);
        var copy = Copy(m, copyId);
        Assert.Equal((400, 200), (copy.X, copy.Y));
        Assert.Equal(Depth.Widget(copy.Widget, copyId), m.Depth);
        var t = m.WidgetEdits[copy.Widget];
        Assert.Equal((Lens.NewWidth, Lens.NewHeight), (t.Width, t.Height));
        Assert.Empty(t.Components);

        // Add at widget depth lands in the widget, relative to its origin.
        m.Add(new TextDef { Id = "hello", Rect = new Rect(410, 205, 50, 20), Text = PropertyValue.Literal("hi") });
        Assert.Equal(new Rect(10, 5, 50, 20), m.WidgetEdits[copy.Widget].Components.Single().Rect);
        Assert.Equal(new Rect(410, 205, 50, 20), Part(m, copyId + ".hello").Rect);

        m.Undo(); m.Undo();
        Assert.Equal(Depth.Layout, m.Depth);
        Assert.Empty(m.WidgetEdits);
    }

    private static List<string> Json(IEnumerable<ComponentDef> defs)
        => defs.Select(c =>
        {
            var clone = LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = [c] }.ToJson()).Components[0];
            clone.Id = "";
            clone.Widget = null;
            return new LayoutFile { BaseImage = "", Components = [clone] }.ToJson();
        }).ToList();

    private static WidgetTemplate TemplateRoundTrip(WidgetTemplate t)
    {
        var path = Path.Combine(TestRun.Root, $"deskwall-lens-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, DeskWall.Designer.Model.Widgets.WidgetTemplateWriter.ToJson(t));
            return WidgetTemplate.Load(path);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Task 3.2, hug contents: at widget depth the widget's size follows its parts, so a
    /// part dragged below the frame grows it, and one dragged back shrinks it again.</summary>
    [Fact]
    public void At_Widget_Depth_The_Size_Hugs_The_Parts()
    {
        var m = TwoDials();
        m.SetDepth(Depth.Widget("dial", "dial-1"));

        m.Move(["dial-1.label"], 0, 40);
        var grown = m.WidgetEdits["dial"];
        Assert.Equal((80, 118), (grown.Width, grown.Height));        // 62 + 40 + 16
        Assert.Equal((100, 50), (Copy(m, "dial-1").X, Copy(m, "dial-1").Y));

        m.Move(["dial-1.label"], 0, -40);
        Assert.Equal((80, 80), (m.WidgetEdits["dial"].Width, m.WidgetEdits["dial"].Height));
    }

    /// <summary>Task 3.2, renormalise: a part dragged left of or above the frame moves the frame's
    /// origin instead. The parts start at (0, 0) again, every copy of the widget shifts by the same
    /// amount so nothing on the canvas jumps, and it is one undo entry.</summary>
    [Fact]
    public void A_Part_Dragged_Negative_Renormalises_And_Every_Copy_Shifts()
    {
        var m = TwoDials();
        m.SetDepth(Depth.Widget("dial", "dial-1"));
        var dial2Label = Part(m, "dial-2.label").Rect;

        m.Move(["dial-1.label"], -10, -70);

        var edited = m.WidgetEdits["dial"];
        Assert.Equal(new Rect(0, 0, 80, 16), edited.Components.Single(c => c.Id == "label").Rect);
        Assert.Equal(new Rect(10, 8, 80, 80), edited.Components.Single(c => c.Id == "dial").Rect);
        Assert.Equal((90, 88), (edited.Width, edited.Height));
        Assert.Equal((90, 42), (Copy(m, "dial-1").X, Copy(m, "dial-1").Y));
        Assert.Equal((290, 42), (Copy(m, "dial-2").X, Copy(m, "dial-2").Y));
        Assert.Equal(new Rect(100, 50, 80, 80), Part(m, "dial-1.dial").Rect);                 // where it was
        Assert.Equal(new Rect(90, 42, 80, 16), Part(m, "dial-1.label").Rect);                 // where it was dragged
        Assert.Equal(dial2Label.Offset(-10, -70), Part(m, "dial-2.label").Rect);             // the other copy follows the widget

        m.Undo();
        Assert.Empty(m.WidgetEdits);
        Assert.Equal((100, 50), (Copy(m, "dial-1").X, Copy(m, "dial-1").Y));
        Assert.Equal((300, 50), (Copy(m, "dial-2").X, Copy(m, "dial-2").Y));
    }

    /// <summary>Task 6.1, Duplicate: a widget of its own under a new key, with one copy, at widget
    /// depth; an edit to it leaves the widget it came from alone; one undo takes both away.</summary>
    [Fact]
    public void DuplicateWidget_Is_A_Widget_Of_Its_Own_Under_A_Free_Key()
    {
        var m = TwoDials();
        var colour = Colour(m, "dial-1.label");
        var copyId = Lens.DuplicateWidget(m, m.Finder()("dial")!, 500, 60);

        var key = Copy(m, copyId).Widget;
        Assert.Equal("hardware-dial-copy", key);
        Assert.Equal("Hardware dial copy", m.WidgetEdits[key].Name);
        Assert.Equal(Depth.Widget(key, copyId), m.Depth);
        Assert.Equal(new Rect(500, 60, 80, 80), Part(m, copyId + ".dial").Rect);

        m.EditAtDepth("Colour", l => ((TextDef)l.Components.Single(c => c.Id == copyId + ".label")).Color = PropertyValue.Literal("#FF0000FF"));
        Assert.Equal("#FF0000FF", Colour(m, copyId + ".label"));
        Assert.Equal(colour, Colour(m, "dial-1.label"));
        Assert.False(m.WidgetEdits.ContainsKey("dial"));

        m.Undo(); m.Undo();
        Assert.Empty(m.WidgetEdits);
        Assert.Equal(2, m.Layout.Copies!.Count);
        Assert.Equal(Depth.Layout, m.Depth);
    }

    /// <summary>Task 6.1, the widget's own fields at widget depth: a rename is one undo entry into the
    /// overlay, keeps the key (plan D2), and a second identical edit is not an entry at all.</summary>
    [Fact]
    public void EditWidget_Renames_Under_The_Same_Key_As_One_Undo_Entry()
    {
        var m = TwoDials();
        Assert.True(Lens.EditWidget(m, "dial", "Rename", d => { d.Name = "CPU gauge"; d.Description = "Mine."; }));
        var t = m.WidgetEdits["dial"];
        Assert.Equal(("dial", "CPU gauge", "Mine."), (t.Key, t.Name, t.Description));
        Assert.Equal(m.Finder()("dial")!.Components.Count, t.Components.Count);
        Assert.All(m.Layout.Copies!, c => Assert.Equal("dial", c.Widget));

        Assert.False(Lens.EditWidget(m, "dial", "Rename", d => d.Name = "CPU gauge"));
        m.Undo();
        Assert.Empty(m.WidgetEdits);
    }
    private static DesignerModel LoosePair() => new(LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg",
          "sources": [ { "name": "hardware", "type": "hardware" } ],
          "components": [
            { "type": "dial", "id": "d", "rect": [200, 300, 80, 80], "fraction": { "bind": "hardware.cpu" } },
            { "type": "text", "id": "t", "rect": [200, 390, 80, 16], "text": "cpu" } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    /// <summary>Critique 2, P3: Make widget names and keys the widget after what is written on it,
    /// says what it shows, and never writes widget.json.</summary>
    [Fact]
    public void Make_Widget_Keys_And_Names_It_After_Its_First_Text_And_Says_What_It_Shows()
    {
        var m = LoosePair();
        var copyId = Lens.MakeWidget(m, ["d", "t"])!;
        var key = Copy(m, copyId).Widget;
        Assert.Equal("cpu", key);
        Assert.Equal("cpu-1", copyId);
        Assert.Equal("cpu", m.WidgetEdits[key].Name);
        Assert.Equal("Shows CPU load.", m.WidgetEdits[key].Description);
    }

    /// <summary>D2 with the critique's rule: the key follows the name until the first Apply, then is fixed.</summary>
    [Fact]
    public void Renaming_Moves_The_Key_Until_The_First_Apply_And_Never_After()
    {
        var home = Path.Combine(TestRun.Root, "rename-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var m = LoosePair();
        m.Path = Path.Combine(home, "layout.json");
        var copyId = Lens.MakeWidget(m, ["d", "t"])!;
        m.SetDepth(Depth.Widget("cpu", copyId));

        Assert.Equal("cpu-gauge", Lens.RenameWidget(m, "cpu", "CPU gauge"));
        Assert.Equal(["cpu-gauge"], m.WidgetEdits.Keys);
        var copy = Assert.Single(m.Layout.Copies!);
        Assert.Equal(("cpu-gauge-1", "cpu-gauge"), (copy.Id, copy.Widget));
        Assert.Equal(Depth.Widget("cpu-gauge", "cpu-gauge-1"), m.Depth);   // stays at widget depth

        m.Undo();                                                          // one entry
        Assert.Equal(["cpu"], m.WidgetEdits.Keys);
        Assert.Equal("cpu", Assert.Single(m.Layout.Copies!).Widget);

        Assert.Equal("cpu-gauge", Lens.RenameWidget(m, "cpu", "CPU gauge"));
        var file = Path.Combine(WidgetCatalog.UserDir, "cpu-gauge.json");
        try
        {
            m.Save();
            Assert.True(File.Exists(file));
            Assert.Equal("cpu-gauge", Lens.RenameWidget(m, "cpu-gauge", "CPU meter"));   // applied: the key stays
            Assert.Equal("CPU meter", m.WidgetEdits["cpu-gauge"].Name);
            Assert.Equal("cpu-gauge", Assert.Single(m.Layout.Copies!).Widget);
        }
        finally { File.Delete(file); }
    }
}
