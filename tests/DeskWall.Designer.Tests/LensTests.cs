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
        var user = Path.Combine(WidgetCatalog.UserDir, "widget.json");
        Directory.CreateDirectory(WidgetCatalog.UserDir);
        File.WriteAllText(user, "not even json");   // a broken file still owns its key
        try
        {
            var m = DesignerModelDepthTests.Model();
            var a = Lens.NewWidget(m, 10, 10);
            var b = Lens.NewWidget(m, 20, 20);
            Assert.Equal("widget-2", Copy(m, a).Widget);
            Assert.Equal("widget-3", Copy(m, b).Widget);
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
        var path = Path.Combine(Path.GetTempPath(), $"deskwall-lens-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, DeskWall.Designer.Model.Widgets.WidgetTemplateWriter.ToJson(t));
            return WidgetTemplate.Load(path);
        }
        finally { File.Delete(path); }
    }
}
