using System.IO;
using DeskWall.Core;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>Writing a widget template back out. The contract is the round trip: whatever
/// <see cref="WidgetTemplate.Load"/> can read, the writer must produce.</summary>
public class WidgetTemplateWriterTests
{
    private static string TempDir(string name)
    {
        var dir = Path.Combine(Paths.RuntimeDir, "writer-tests", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static WidgetTemplate SaveAndLoad(WidgetTemplate t, string dirName)
    {
        var path = Path.Combine(TempDir(dirName), t.Key + ".json");
        File.WriteAllText(path, WidgetTemplateWriter.ToJson(t));
        return WidgetTemplate.Load(path);
    }

    [Theory]
    [MemberData(nameof(WidgetDocumentTests.ShippedKeys), MemberType = typeof(WidgetDocumentTests))]
    public void Every_Shipped_Widget_Survives_A_Write_And_A_Read(string key)
    {
        var original = TestRepo.Widgets().First(t => t.Key == key);
        var written = SaveAndLoad(original, "roundtrip-" + key);
        WidgetDocumentTests.AssertSameTemplate(original, written);
    }

    [Theory]
    [MemberData(nameof(WidgetDocumentTests.ShippedKeys), MemberType = typeof(WidgetDocumentTests))]
    public void And_So_Does_One_That_Went_Through_The_Editor(string key)
    {
        var original = TestRepo.Widgets().First(t => t.Key == key);
        var edited = WidgetDocument.FromTemplate(original, null).ToTemplate();
        WidgetDocumentTests.AssertSameTemplate(original, SaveAndLoad(edited, "edited-" + key));
    }

    [Fact]
    public void A_Top_Anchor_And_A_Null_Requires_Are_Left_Out_Of_The_File()
    {
        var doc = WidgetDocument.New();
        doc.Name = "Plain";
        doc.Description = "d";
        var json = WidgetTemplateWriter.ToJson(doc.ToTemplate());
        Assert.DoesNotContain("\"anchor\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"requires\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"size\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Bottom_Anchor_And_A_Requires_Are_Written()
    {
        var doc = WidgetDocument.New();
        doc.Name = "Anchored";
        doc.Description = "d";
        doc.Anchor = "bottom";
        doc.Requires = "Needs a thing";
        var written = SaveAndLoad(doc.ToTemplate(), "anchored");
        Assert.Equal("bottom", written.Anchor);
        Assert.Equal("Needs a thing", written.Requires);
    }

    // ---- saving -------------------------------------------------------------------------------

    private static WidgetDocument Named(string name)
    {
        var doc = WidgetDocument.New();
        doc.Name = name;
        doc.Description = "Made in a test.";
        doc.AddPart(PartKind.Text);
        return doc;
    }

    [Fact]
    public void Save_Writes_The_Key_Named_File_Into_The_User_Dir()
    {
        var dir = TempDir("save");
        var doc = Named("My dial");

        var path = WidgetTemplateWriter.Save(doc, [], dir);

        Assert.Equal(Path.Combine(dir, "my-dial.json"), path);
        Assert.Equal("My dial", WidgetTemplate.Load(path).Name);
        Assert.Equal(path, doc.Path);
        Assert.Equal("my-dial", doc.EditingKey);
    }

    [Fact]
    public void Save_Creates_The_User_Dir()
    {
        var dir = Path.Combine(TempDir("makedir"), "widgets");
        WidgetTemplateWriter.Save(Named("Fresh"), [], dir);
        Assert.True(File.Exists(Path.Combine(dir, "fresh.json")));
    }

    [Fact]
    public void A_New_Widget_May_Not_Take_A_Shipped_Widgets_Key()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => WidgetTemplateWriter.Save(Named("Weather"), ["weather"], TempDir("clash")));
        Assert.Equal("A shipped widget is already called 'Weather'. Pick another name.", ex.Message);
    }

    /// <summary>A user file that already overrides a shipped key keeps working: it is only a NEW
    /// or a RENAMED document that may not land on one.</summary>
    [Fact]
    public void Re_Saving_A_User_File_That_Already_Overrides_A_Shipped_Key_Is_Allowed()
    {
        var dir = TempDir("override");
        var doc = Named("Weather");
        doc.EditingKey = "weather";
        doc.Path = Path.Combine(dir, "weather.json");

        var path = WidgetTemplateWriter.Save(doc, ["weather"], dir);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_Rename_Deletes_The_File_It_Was_Saved_Under()
    {
        var dir = TempDir("rename");
        var doc = Named("First name");
        var first = WidgetTemplateWriter.Save(doc, [], dir);
        Assert.True(File.Exists(first));

        doc.Name = "Second name";
        var second = WidgetTemplateWriter.Save(doc, [], dir);

        Assert.True(File.Exists(second));
        Assert.False(File.Exists(first));
        Assert.Equal("second-name", doc.EditingKey);
    }

    [Fact]
    public void Six_Knobs_Are_Refused_Rather_Than_Written_Unreadable()
    {
        var doc = Named("Too many");
        var part = doc.Model.Layout.Components[0].Id;
        foreach (var prop in new[] { "Text", "Font", "Size", "Weight", "Color", "Align" })
            doc.ToggleAdjustable(part, prop);

        var ex = Assert.Throws<InvalidOperationException>(() => WidgetTemplateWriter.Save(doc, [], TempDir("knobs")));
        Assert.Contains("5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Widget_With_No_Description_Is_Refused()
    {
        var doc = WidgetDocument.New();
        doc.Name = "Nameless";
        doc.Description = "  ";
        var ex = Assert.Throws<InvalidOperationException>(() => WidgetTemplateWriter.Save(doc, [], TempDir("nodesc")));
        Assert.Contains("description", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
