using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using Xunit;

namespace DeskWall.Core.Tests.Widgets;

internal static class Repo
{
    public static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return d.FullName;
            throw new InvalidOperationException("repo root not found");
        }
    }

    public static string WidgetsDir => Path.Combine(Root, "widgets");

    public static Func<string, WidgetTemplate?> Shipped() => WidgetCatalog.Finder(WidgetsDir);

    /// <summary>A fresh, empty folder under the temp dir (never the runtime dir).</summary>
    public static string TempDir(string name)
    {
        var dir = Path.Combine(TestRun.Root, "core-widgets", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Write(string dir, string name, string json)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }
}

public class WidgetTemplateTests
{
    public static TheoryData<string> ShippedFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(Repo.WidgetsDir, "*.json")) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedFiles))]
    public void Every_Shipped_Widget_Loads(string file)
    {
        var t = WidgetTemplate.Load(Path.Combine(Repo.WidgetsDir, file));
        Assert.Equal(Path.GetFileNameWithoutExtension(file), t.Key);
        Assert.NotEmpty(t.Components);
    }

    [Fact]
    public void Fields_Are_Read()
    {
        var t = WidgetTemplate.Load(Path.Combine(Repo.WidgetsDir, "dial.json"));
        Assert.Equal(("Hardware dial", 80, 80, "top"), (t.Name, t.Width, t.Height, t.Anchor));
        Assert.Equal("hardware", Assert.Single(t.Sources).Name);
        Assert.Equal(["dial", "value", "label"], t.Components.Select(c => c.Id));
        Assert.Equal(KnobType.Choice, t.Knobs[0].Type);
        Assert.Equal(4, t.Knobs[0].Choices!.Count);
        Assert.Equal((0d, 1d), (t.Knobs[1].Min!.Value, t.Knobs[1].Max!.Value));
        Assert.Equal("bottom", WidgetTemplate.Load(Path.Combine(Repo.WidgetsDir, "drives.json")).Anchor);
    }

    [Theory]
    [InlineData("no-name", """{ "description": "d", "size": [10, 10] }""", "name")]
    [InlineData("bad-size", """{ "name": "X", "description": "d", "size": [10] }""", "size")]
    [InlineData("bad-anchor", """{ "name": "X", "description": "d", "size": [10, 10], "anchor": "middle" }""", "anchor")]
    [InlineData("bad-knob", """{ "name": "X", "description": "d", "size": [10, 10], "knobs": [ { "id": "weird", "type": "nope", "sets": [] } ] }""", "weird")]
    [InlineData("dotted-id", """{ "name": "X", "description": "d", "size": [10, 10], "components": [ { "type": "text", "id": "a.b", "rect": [0,0,1,1], "text": "x" } ] }""", "a.b")]
    public void A_Bad_File_Names_The_File_And_Field(string name, string json, string field)
    {
        var path = Repo.Write(Repo.TempDir("rejects-" + name), name + ".json", json);
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void More_Than_Five_Knobs_Is_Rejected()
    {
        var knobs = string.Join(",", Enumerable.Range(0, 6).Select(i => $$"""{ "id": "k{{i}}", "type": "text", "sets": [] }"""));
        var path = Repo.Write(Repo.TempDir("rejects-knobs"), "many.json", $$"""{ "name": "X", "description": "d", "size": [10, 10], "knobs": [ {{knobs}} ] }""");
        var ex = Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
        Assert.Contains("6 knobs", ex.Message);
    }

    [Fact]
    public void Unparseable_Json_Is_A_FormatException()
    {
        var path = Repo.Write(Repo.TempDir("rejects-json"), "broken.json", "{ \"name\": ");
        Assert.Throws<FormatException>(() => WidgetTemplate.Load(path));
    }

    private const string Plain = """{ "name": "N", "description": "d", "size": [10, 10], "components": [ { "type": "text", "id": "t", "rect": [0,0,1,1], "text": "x" } ] }""";

    [Fact]
    public void A_Later_Dir_Shadows_An_Earlier_One_By_Key()
    {
        var shipped = Repo.TempDir("shadow-shipped");
        var user = Repo.TempDir("shadow-user");
        Repo.Write(shipped, "clock.json", Plain.Replace("\"N\"", "\"Shipped\""));
        Repo.Write(shipped, "only-shipped.json", Plain);
        Repo.Write(user, "clock.json", Plain.Replace("\"N\"", "\"Mine\""));

        var find = WidgetCatalog.Finder(shipped, user);
        var clock = find("clock")!;
        Assert.Equal("Mine", clock.Name);
        Assert.True(clock.OverridesShipped);
        Assert.False(find("only-shipped")!.OverridesShipped);
        Assert.Null(find("nope"));
        Assert.Null(find("..\\clock"));
    }

    [Fact]
    public void A_Finder_Reads_Only_What_Is_Asked_And_Caches_It()
    {
        var dir = Repo.TempDir("finder-cache");
        Repo.Write(dir, "good.json", Plain);
        Repo.Write(dir, "broken.json", "not json");

        var find = WidgetCatalog.Finder(dir);
        var first = find("good");
        Assert.Same(first, find("GOOD"));
        Assert.Throws<FormatException>(() => find("broken"));
    }
}
