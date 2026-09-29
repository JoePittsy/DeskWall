using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using Xunit;

namespace DeskWall.Core.Tests.Widgets;

public class KnobSetsTests
{
    private static WidgetTemplate Shipped(string key) => Repo.Shipped()(key)!;

    private static LayoutFile PartsOf(WidgetTemplate t)
        => LayoutFile.Parse(new LayoutFile { BaseImage = "", Sources = [.. t.Sources], Components = [.. t.Components] }.ToJson());

    private static T Part<T>(LayoutFile parts, string id) where T : ComponentDef => (T)parts.Components.Single(c => c.Id == id);

    [Fact]
    public void Metric_Writes_Two_Bindings_And_A_Caption()
    {
        var t = Shipped("dial");
        var parts = PartsOf(t);
        var failed = KnobSets.ApplyKnob(parts, t, t.Knobs[0], "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu");

        Assert.Empty(failed);
        Assert.Equal("hardware.gpu", Part<DialDef>(parts, "dial").Fraction.Binding!.ToString());
        Assert.Equal("hardware.gpuPct | \"{0}%\"", Part<TextDef>(parts, "value").Text.Binding!.ToString());
        Assert.Equal("gpu", Part<TextDef>(parts, "label").Text.LiteralText);
    }

    [Fact]
    public void Town_Substitutes_Two_Tokens_Into_One_Url_From_The_Template_Every_Time()
    {
        var t = Shipped("weather");
        var parts = PartsOf(t);
        Assert.Empty(KnobSets.ApplyKnob(parts, t, t.Knobs[0], "Leeds||53.8||-1.5"));
        Assert.Empty(KnobSets.ApplyKnob(parts, t, t.Knobs[0], "York||53.9||-1.1"));

        var url = parts.Sources.Single().Settings["url"];
        Assert.Contains("latitude=53.9&longitude=-1.1&", url);
        Assert.DoesNotContain("{", url);
    }

    [Fact]
    public void Drive_Substitutes_Inside_A_Binding_And_Stays_Bound()
    {
        var dir = Repo.TempDir("knobsets-drive");
        var t = WidgetTemplate.Load(Repo.Write(dir, "drive.json", """
        { "name": "Drive", "description": "d", "size": [172, 30],
          "sources": [ { "name": "disks", "type": "disks" } ],
          "components": [
            { "type": "bar", "id": "bar", "rect": [0, 20, 172, 6], "fraction": { "bind": "disks.drives[{drive}].usedFraction" } },
            { "type": "text", "id": "free", "rect": [0, 0, 172, 20], "text": { "bind": "disks.drives[{drive}].freeGB | \"{0:N0} GB\"" } } ],
          "knobs": [ { "id": "drive", "label": "Drive", "type": "drive", "default": "C",
                       "sets": [ "components.bar.fraction:{drive}", "components.free.text:{drive}" ] } ] }
        """));
        var parts = PartsOf(t);
        Assert.Empty(KnobSets.ApplyKnob(parts, t, t.Knobs[0], "D"));

        Assert.Equal("disks.drives[D].usedFraction", Part<BarDef>(parts, "bar").Fraction.Binding!.ToString());
        Assert.Equal("disks.drives[D].freeGB | \"{0:N0} GB\"", Part<TextDef>(parts, "free").Text.Binding!.ToString());
    }

    [Fact]
    public void Plain_Paths_Rect_Z_Every_And_Settings()
    {
        var t = Shipped("headline");
        var parts = PartsOf(t);
        Assert.True(KnobSets.Apply(parts, "components.headline.color", PropertyValue.Literal("#FF00FF00")));
        Assert.True(KnobSets.Apply(parts, "components.headline.rect", PropertyValue.Literal("1,2,3,4")));
        Assert.True(KnobSets.Apply(parts, "components.headline.z", PropertyValue.Literal(7)));
        Assert.True(KnobSets.Apply(parts, "sources.feed.every", PropertyValue.Literal(60)));
        Assert.True(KnobSets.Apply(parts, "sources.feed.settings.max", PropertyValue.Literal("3")));

        var h = Part<TextDef>(parts, "headline");
        Assert.Equal(("#FF00FF00", new Rect(1, 2, 3, 4), 7), (h.Color.LiteralText, h.Rect, h.Z));
        Assert.Equal((60, "3"), (parts.Sources[0].EverySeconds, parts.Sources[0].Settings["max"]));
        Assert.Equal("1,2,3,4", KnobSets.Get(parts, "components.headline.rect")!.LiteralText);
    }

    [Fact]
    public void Four_Segments_Reach_A_Repeater_Child_And_Hidden_Removes_It()
    {
        var parts = PartsOf(Shipped("drives"));
        Assert.True(KnobSets.Apply(parts, "components.drives.letter.size", PropertyValue.Literal(20)));
        var repeater = Part<RepeaterDef>(parts, "drives");
        Assert.Equal("20", ((TextDef)repeater.Template.Single(c => c.Id == "letter")).Size.LiteralText);

        Assert.True(KnobSets.Apply(parts, "components.drives.bar.hidden", PropertyValue.Literal("true")));
        Assert.DoesNotContain(repeater.Template, c => c.Id == "bar");
    }

    [Theory]
    [InlineData("components.nope.text")]
    [InlineData("components.headline.nope")]
    [InlineData("components.headline.x.size")]
    [InlineData("sources.nope.every")]
    [InlineData("sources.feed.nope")]
    [InlineData("widgets.x.y")]
    public void A_Path_Naming_Nothing_Is_Not_Applied(string path)
        => Assert.False(KnobSets.Apply(PartsOf(Shipped("headline")), path, PropertyValue.Literal("1")));

    [Fact]
    public void A_Binding_Cannot_Go_Into_A_Setting()
        => Assert.False(KnobSets.Apply(PartsOf(Shipped("headline")), "sources.feed.settings.url",
            PropertyValue.Bound(DeskWall.Core.Bindings.Binding.Parse("time.now"))));
}
