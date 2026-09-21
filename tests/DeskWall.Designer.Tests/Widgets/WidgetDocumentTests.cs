using System.IO;
using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>The widget editor's document. Everything here is the model only: no window is
/// constructed, so these run on the xUnit thread like the rest of the designer's model tests.</summary>
public class WidgetDocumentTests
{
    public static TheoryData<string> ShippedKeys()
    {
        var data = new TheoryData<string>();
        foreach (var t in TestRepo.Widgets()) data.Add(t.Key);
        return data;
    }

    private static WidgetTemplate Shipped(string key) => TestRepo.Widgets().First(t => t.Key == key);

    // ---- new ----------------------------------------------------------------------------------

    [Fact]
    public void New_Starts_Empty_At_172_By_40()
    {
        var doc = WidgetDocument.New();
        Assert.Equal("New widget", doc.Name);
        Assert.Equal(172, doc.Model.Signature.Width);
        Assert.Equal(40, doc.Model.Signature.Height);
        Assert.Empty(doc.Model.Layout.Components);
        Assert.Empty(doc.Model.Layout.Sources);
        Assert.Equal("", doc.Model.Layout.BaseImage);
        Assert.Equal("top", doc.Anchor);
        Assert.Null(doc.Path);
        Assert.Null(doc.EditingKey);
        Assert.Empty(doc.Adjustables);
        Assert.Empty(doc.PassThroughKnobs);
    }

    // ---- round trip ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ShippedKeys))]
    public void FromTemplate_Then_ToTemplate_Is_The_Same_Widget(string key)
    {
        var original = Shipped(key);
        var doc = WidgetDocument.FromTemplate(original, path: null);
        AssertSameTemplate(original, doc.ToTemplate());
    }

    [Fact]
    public void FromTemplate_Deep_Copies_So_Editing_Cannot_Reach_The_Catalog()
    {
        var original = Shipped("text");
        var doc = WidgetDocument.FromTemplate(original, null);
        doc.Model.Move([doc.Model.Layout.Components[0].Id], 17, 19);

        Assert.Equal(new Rect(0, 0, 172, 24), original.Components[0].Rect);
        Assert.Equal(new Rect(17, 19, 172, 24), doc.Model.Layout.Components[0].Rect);
    }

    [Fact]
    public void A_Simple_Knob_Becomes_An_Adjustable_And_A_Composite_Passes_Through()
    {
        var doc = WidgetDocument.FromTemplate(Shipped("dial"), null);

        var warn = Assert.Single(doc.Adjustables);
        Assert.Equal("warnAt", warn.Id);
        Assert.Equal("Warn at", warn.Label);
        Assert.Equal("dial", warn.ComponentId);
        Assert.Equal("Threshold", warn.Property);

        var metric = Assert.Single(doc.PassThroughKnobs);
        Assert.Equal("metric", metric.Id);
    }

    [Fact]
    public void A_Token_Knob_Passes_Through()
    {
        var doc = WidgetDocument.FromTemplate(Shipped("weather"), null);
        Assert.Empty(doc.Adjustables);
        Assert.Equal("town", Assert.Single(doc.PassThroughKnobs).Id);
    }

    [Fact]
    public void A_Source_Setting_Knob_Becomes_An_Adjustable()
    {
        var doc = WidgetDocument.FromTemplate(Shipped("headline"), null);
        var url = Assert.Single(doc.Adjustables);
        Assert.Equal("feed", url.SourceName);
        Assert.Equal("url", url.SettingKey);
        Assert.Null(url.ComponentId);
    }

    // ---- parts ---------------------------------------------------------------------------------

    [Fact]
    public void AddPart_Names_The_Part_After_Its_Kind_And_Steps_Each_One_Down()
    {
        var doc = WidgetDocument.New();
        var first = doc.AddPart(PartKind.Text);
        var second = doc.AddPart(PartKind.Text);
        var third = doc.AddPart(PartKind.Dial);

        Assert.Equal("text", first.Id);
        Assert.Equal("text-2", second.Id);
        Assert.Equal("dial", third.Id);
        Assert.Equal(new Rect(0, 0, 120, 24), first.Rect);
        Assert.Equal(new Rect(8, 8, 120, 24), second.Rect);
        Assert.Equal(new Rect(16, 16, 80, 80), third.Rect);
        Assert.IsType<TextDef>(first);
        Assert.IsType<DialDef>(third);
    }

    [Fact]
    public void AddPart_Makes_Each_Kind_With_Its_Own_Defaults()
    {
        var doc = WidgetDocument.New();
        Assert.Equal("Text", Assert.IsType<TextDef>(doc.AddPart(PartKind.Text)).Text.LiteralText);
        Assert.Equal("", Assert.IsType<ImageDef>(doc.AddPart(PartKind.Image)).Source.LiteralText);
        Assert.IsType<BarDef>(doc.AddPart(PartKind.Bar));
        Assert.Equal("0.5", Assert.IsType<DialDef>(doc.AddPart(PartKind.Dial)).Fraction.LiteralText);
    }

    // ---- size ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(200, 90, 200, 90)]
    [InlineData(0, 0, 8, 8)]
    [InlineData(-40, 3, 8, 8)]
    [InlineData(9000, 9000, 3440, 1440)]
    public void Resize_Clamps_To_The_Allowed_Range(int w, int h, int expectedW, int expectedH)
    {
        var doc = WidgetDocument.New();
        doc.Resize(w, h);
        Assert.Equal(expectedW, doc.Model.Signature.Width);
        Assert.Equal(expectedH, doc.Model.Signature.Height);
    }

    [Fact]
    public void Resize_Is_One_Undo_Entry_And_Undo_Puts_The_Size_Back()
    {
        var doc = WidgetDocument.New();
        doc.Resize(300, 120);
        Assert.True(doc.Model.CanUndo);

        doc.Model.Undo();
        Assert.Equal(172, doc.Model.Signature.Width);
        Assert.Equal(40, doc.Model.Signature.Height);

        doc.Model.Redo();
        Assert.Equal(300, doc.Model.Signature.Width);
        Assert.Equal(120, doc.Model.Signature.Height);
    }

    // ---- fit to parts ---------------------------------------------------------------------------

    [Fact]
    public void FitToParts_Shifts_The_Parts_To_The_Origin_And_Sizes_The_Widget_To_Them()
    {
        var doc = WidgetDocument.New();
        var a = doc.AddPart(PartKind.Text);
        var b = doc.AddPart(PartKind.Dial);
        doc.Model.SetRect(a.Id, new Rect(30, 20, 100, 24));
        doc.Model.SetRect(b.Id, new Rect(10, 60, 40, 40));

        doc.FitToParts();

        Assert.Equal(new Rect(20, 0, 100, 24), doc.Model.Find(a.Id)!.Rect);
        Assert.Equal(new Rect(0, 40, 40, 40), doc.Model.Find(b.Id)!.Rect);
        Assert.Equal(120, doc.Model.Signature.Width);
        Assert.Equal(80, doc.Model.Signature.Height);
    }

    [Fact]
    public void FitToParts_Is_One_Undo_Entry()
    {
        var doc = WidgetDocument.New();
        var a = doc.AddPart(PartKind.Text);
        doc.Model.SetRect(a.Id, new Rect(30, 20, 100, 24));
        var before = doc.Model.Layout.ToJson();

        doc.FitToParts();
        doc.Model.Undo();

        Assert.Equal(before, doc.Model.Layout.ToJson());
        Assert.Equal(172, doc.Model.Signature.Width);
    }

    [Fact]
    public void FitToParts_With_No_Parts_Does_Nothing()
    {
        var doc = WidgetDocument.New();
        doc.FitToParts();
        Assert.False(doc.Model.CanUndo);
        Assert.Equal(172, doc.Model.Signature.Width);
    }

    // ---- sources ---------------------------------------------------------------------------------

    [Fact]
    public void Sources_Add_Replace_And_Remove()
    {
        var doc = WidgetDocument.New();
        doc.AddSource(new SourceDef { Name = "hardware", Type = "hardware" });
        Assert.Equal("hardware", Assert.Single(doc.Model.Layout.Sources).Name);

        doc.ReplaceSource("hardware", new SourceDef { Name = "hw", Type = "hardware", EverySeconds = 30 });
        Assert.Equal("hw", Assert.Single(doc.Model.Layout.Sources).Name);
        Assert.Equal(30, doc.Model.Layout.Sources[0].EverySeconds);

        doc.RemoveSource("hw");
        Assert.Empty(doc.Model.Layout.Sources);
    }

    // ---- the key --------------------------------------------------------------------------------

    [Theory]
    [InlineData("My dial", "my-dial")]
    [InlineData("GPU \u00b0C", "gpu-c")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("Weather copy", "weather-copy")]
    [InlineData("", "widget")]
    [InlineData("!!!", "widget")]
    [InlineData("Disk 2", "disk-2")]
    public void Key_Is_A_Slug_Of_The_Name(string name, string expected)
    {
        var doc = WidgetDocument.New();
        doc.Name = name;
        Assert.Equal(expected, doc.Key);
    }

    [Fact]
    public void FromTemplate_Remembers_Where_It_Came_From()
    {
        var path = Path.Combine(TestRepo.WidgetsDir, "text.json");
        var doc = WidgetDocument.FromTemplate(Shipped("text"), path);
        Assert.Equal(path, doc.Path);
        Assert.Equal("text", doc.EditingKey);
    }

    // ---- comparison ------------------------------------------------------------------------------

    internal static void AssertSameTemplate(WidgetTemplate expected, WidgetTemplate actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Anchor, actual.Anchor);
        Assert.Equal(expected.Requires, actual.Requires);

        Assert.Equal(expected.Sources.Count, actual.Sources.Count);
        for (var i = 0; i < expected.Sources.Count; i++)
        {
            Assert.Equal(expected.Sources[i].Name, actual.Sources[i].Name);
            Assert.Equal(expected.Sources[i].Type, actual.Sources[i].Type);
            Assert.Equal(expected.Sources[i].EverySeconds, actual.Sources[i].EverySeconds);
            Assert.Equal(expected.Sources[i].Settings, actual.Sources[i].Settings);
        }

        Assert.Equal(ComponentsJson(expected), ComponentsJson(actual));

        Assert.Equal(expected.Knobs.Count, actual.Knobs.Count);
        for (var i = 0; i < expected.Knobs.Count; i++)
        {
            var e = expected.Knobs[i];
            var a = actual.Knobs[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Label, a.Label);
            Assert.Equal(e.Type, a.Type);
            Assert.Equal(e.Default, a.Default);
            Assert.Equal(e.Sets, a.Sets);
            Assert.Equal(e.Choices, a.Choices);
            Assert.Equal(e.Min, a.Min);
            Assert.Equal(e.Max, a.Max);
        }
    }

    private static string ComponentsJson(WidgetTemplate t)
        => new LayoutFile { BaseImage = "", Components = t.Components.ToList() }.ToJson();
}
