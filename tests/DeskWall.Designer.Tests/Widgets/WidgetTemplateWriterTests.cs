using System.IO;
using DeskWall.Core;
using DeskWall.Designer.Model.Widgets;
using Xunit;
using DeskWall.Core.Widgets;

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
        var edited = WidgetDocument.FromTemplate(original).ToTemplate();
        WidgetDocumentTests.AssertSameTemplate(original, SaveAndLoad(edited, "edited-" + key));
    }

    [Fact]
    public void A_Top_Anchor_And_A_Null_Requires_Are_Left_Out_Of_The_File()
    {
        var doc = Drafts.New();
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
        var doc = Drafts.New();
        doc.Name = "Anchored";
        doc.Description = "d";
        doc.Anchor = "bottom";
        doc.Requires = "Needs a thing";
        var written = SaveAndLoad(doc.ToTemplate(), "anchored");
        Assert.Equal("bottom", written.Anchor);
        Assert.Equal("Needs a thing", written.Requires);
    }
}
