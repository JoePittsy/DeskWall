using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using Xunit;

namespace DeskWall.Core.Tests.Widgets;

public class WidgetExpanderTests
{
    private static LayoutFile V2(params WidgetCopy[] copies) => new()
    {
        Version = 2,
        BaseImage = "wall.jpg",
        Sources = [new SourceDef { Name = "time", Type = "time" }],
        Components = [new TextDef { Id = "note", Rect = new Rect(1, 1, 10, 10), Text = PropertyValue.Literal("hi") }],
        Copies = [.. copies],
    };

    private static WidgetCopy Copy(string id, string widget, int x = 0, int y = 0, int z = 0) => new() { Id = id, Widget = widget, X = x, Y = y, Z = z };

    private static T Part<T>(Expansion e, string id) where T : ComponentDef => (T)e.Layout.Components.Single(c => c.Id == id);

    [Fact]
    public void A_V1_File_Is_Its_Own_Expansion()
    {
        var v1 = LayoutFile.Load(Path.Combine(Repo.Root, "tests", "fixtures", "layouts-v1", "clock-disks.json"));
        var e = WidgetExpander.Expand(v1, _ => throw new InvalidOperationException("not called"));
        Assert.Same(v1, e.Layout);
        Assert.Empty(e.Problems);
    }

    [Fact]
    public void A_Plain_Copy_Gets_Prefixed_Ids_Offset_Rects_And_Added_Z()
    {
        var layout = V2(Copy("dial-2", "dial", 3312, 400, 10));
        var e = WidgetExpander.Expand(layout, Repo.Shipped());

        Assert.Empty(e.Problems);
        Assert.Equal(["dial"], e.WidgetKeys);
        Assert.Null(e.Layout.Copies);
        Assert.NotNull(layout.Copies); // the input is left alone
        Assert.Equal(["note", "dial-2.dial", "dial-2.value", "dial-2.label"], e.Layout.Components.Select(c => c.Id));
        var value = Part<TextDef>(e, "dial-2.value");
        Assert.Equal((new Rect(3312, 426, 80, 28), 12, "dial-2"), (value.Rect, value.Z, value.Widget));
        Assert.Null(Part<TextDef>(e, "note").Widget);
        Assert.Equal(["time", "hardware"], e.Layout.Sources.Select(s => s.Name));
    }

    [Fact]
    public void Knob_Defaults_Apply_And_A_Set_Knob_Wins()
    {
        var gpu = Copy("dial-1", "dial");
        gpu.Knobs["metric"] = "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu";
        var e = WidgetExpander.Expand(V2(gpu, Copy("dial-2", "dial")), Repo.Shipped());

        Assert.Equal("gpu", Part<TextDef>(e, "dial-1.label").Text.LiteralText);
        Assert.Equal("cpu", Part<TextDef>(e, "dial-2.label").Text.LiteralText);
        Assert.Equal("0.9", Part<DialDef>(e, "dial-2.dial").Threshold.LiteralText);
    }

    [Fact]
    public void Town_Knob_Lands_Both_Tokens_In_The_Source()
    {
        var c = Copy("weather-1", "weather");
        c.Knobs["town"] = "York||53.9||-1.1";
        var e = WidgetExpander.Expand(V2(c), Repo.Shipped());
        Assert.Contains("latitude=53.9&longitude=-1.1&", e.Layout.Sources.Single(s => s.Name == "weather").Settings["url"]);
    }

    [Fact]
    public void An_Override_Beats_A_Knob()
    {
        var c = Copy("dial-1", "dial");
        c.Knobs["metric"] = "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu";
        c.Overrides["components.label.text"] = PropertyValue.Literal("graphics");
        var e = WidgetExpander.Expand(V2(c), Repo.Shipped());
        Assert.Equal("graphics", Part<TextDef>(e, "dial-1.label").Text.LiteralText);
        Assert.Equal("hardware.gpu", Part<DialDef>(e, "dial-1.dial").Fraction.Binding!.ToString());
    }

    [Fact]
    public void Rect_Is_Relative_To_The_Copy_Hidden_Drops_A_Part_And_A_Repeater_Child_Can_Be_Overridden()
    {
        var dial = Copy("dial-1", "dial", 100, 200);
        dial.Overrides["components.label.hidden"] = PropertyValue.Literal("true");
        dial.Overrides["components.label.size"] = PropertyValue.Literal(30); // on a hidden part: not an orphan
        dial.Overrides["components.value.rect"] = PropertyValue.Literal("0,60,80,18");
        var drives = Copy("drives-1", "drives");
        drives.Overrides["components.drives.letter.size"] = PropertyValue.Literal(20);

        var e = WidgetExpander.Expand(V2(dial, drives), Repo.Shipped());

        Assert.Empty(e.Problems);
        Assert.DoesNotContain(e.Layout.Components, c => c.Id == "dial-1.label");
        Assert.Equal(new Rect(100, 260, 80, 18), Part<TextDef>(e, "dial-1.value").Rect);
        var letter = (TextDef)Part<RepeaterDef>(e, "drives-1.drives").Template.Single(c => c.Id == "letter");
        Assert.Equal("20", letter.Size.LiteralText);
    }

    [Fact]
    public void Orphans_Missing_And_Broken_Are_Reported_And_The_Rest_Still_Emits()
    {
        var dir = Repo.TempDir("expander-broken");
        File.Copy(Path.Combine(Repo.WidgetsDir, "clock.json"), Path.Combine(dir, "clock.json"));
        Repo.Write(dir, "broken.json", "{ nope");
        var find = WidgetCatalog.Finder(Repo.WidgetsDir, dir);

        var orphaned = Copy("clock-1", "clock");
        orphaned.Overrides["components.gone.color"] = PropertyValue.Literal("#FF000000");
        orphaned.Overrides["components.clock.color"] = PropertyValue.Literal("#FF00FF00");
        orphaned.Knobs["gone"] = "x";
        var e = WidgetExpander.Expand(V2(orphaned, Copy("x-1", "missing"), Copy("b-1", "broken")), find);

        Assert.Equal(
            [(ExpandProblemKind.OrphanKnob, "clock-1", "gone"), (ExpandProblemKind.OrphanOverride, "clock-1", "components.gone.color"),
             (ExpandProblemKind.MissingWidget, "x-1", "missing"), (ExpandProblemKind.BrokenWidget, "b-1", "broken")],
            e.Problems.Select(p => (p.Kind, p.CopyId, p.Detail.Split(':')[0])));
        Assert.Equal("#FF00FF00", Part<TextDef>(e, "clock-1.clock").Color.LiteralText);
        Assert.Equal(["clock", "missing", "broken"], e.WidgetKeys);
    }

    [Fact]
    public void Identical_Sources_Are_Shared_And_Different_Ones_Renamed_With_Their_Bindings()
    {
        var bbc = Copy("headline-1", "headline");
        var other = Copy("headline-2", "headline");
        other.Knobs["url"] = "https://example.com/feed.xml";
        var third = Copy("headline-3", "headline");
        third.Knobs["url"] = "https://example.com/feed.xml";

        var e = WidgetExpander.Expand(V2(Copy("dial-1", "dial"), Copy("dial-2", "dial"), bbc, other, third), Repo.Shipped());

        Assert.Equal(["time", "hardware", "feed", "feed2"], e.Layout.Sources.Select(s => s.Name));
        Assert.Equal("https://example.com/feed.xml", e.Layout.Sources[3].Settings["url"]);
        Assert.Equal("feed.items[0].title", Part<TextDef>(e, "headline-1.headline").Text.Binding!.ToString());
        Assert.Equal("feed2.items[0].title", Part<TextDef>(e, "headline-2.headline").Text.Binding!.ToString());
        Assert.Equal("feed2.items[0].title", Part<TextDef>(e, "headline-3.headline").Text.Binding!.ToString());
    }

    [Fact]
    public void A_Layout_Source_Of_The_Same_Name_Renames_The_Copy_Source()
    {
        var layout = V2(Copy("headline-1", "headline"));
        layout.Sources.Add(new SourceDef { Name = "feed", Type = "command" });
        var e = WidgetExpander.Expand(layout, Repo.Shipped());
        Assert.Equal("rss", e.Layout.Sources.Single(s => s.Name == "feed2").Type);
        Assert.Equal("feed2.items[0].title", Part<TextDef>(e, "headline-1.headline").Text.Binding!.ToString());
    }
}
