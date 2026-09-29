using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using Xunit;
using Xunit.Abstractions;

namespace DeskWall.Core.Tests.Widgets;

public class LayoutMigratorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("column-system.json")]
    [InlineData("clock-disks.json")]
    [InlineData("steam-recent.json")]
    public void The_Repo_V1_Layouts_Migrate_Equivalent(string file)
    {
        var v1 = LayoutFile.Load(Path.Combine(Repo.Root, "tests", "fixtures", "layouts-v1", file));
        var before = v1.ToJson();
        var r = LayoutMigrator.Migrate(v1, Repo.Shipped());
        foreach (var n in r.Notes) output.WriteLine(n);

        Assert.True(r.Equivalent, string.Join(Environment.NewLine, r.Notes));
        Assert.Equal(before, v1.ToJson()); // the input is untouched
        Assert.Equal(2, r.V2.Version);
        Assert.Null(r.V2.Widgets);
        Assert.Equal(v1.Widgets!.Keys.Order(), r.V2.Copies!.Select(c => c.Id).Order());
        Assert.Empty(r.V2.Components); // every stamped part became a copy
        Assert.Empty(WidgetExpander.Expand(LayoutFile.Parse(r.V2.ToJson()), Repo.Shipped()).Problems);
    }

    [Fact]
    public void Migrating_Twice_Is_A_No_Op()
    {
        var once = LayoutMigrator.Migrate(LayoutFile.Load(Path.Combine(Repo.Root, "tests", "fixtures", "layouts-v1", "column-system.json")), Repo.Shipped());
        var twice = LayoutMigrator.Migrate(once.V2, Repo.Shipped());
        Assert.Same(once.V2, twice.V2);
        Assert.True(twice.Equivalent);
    }

    // ---- fixtures: a v2 layout expanded and dressed as a v1 stamped file, then drifted ------------

    private static WidgetCopy Copy(string id, string widget, int x, int y, params (string Knob, string Value)[] knobs)
    {
        var c = new WidgetCopy { Id = id, Widget = widget, X = x, Y = y };
        foreach (var (k, v) in knobs) c.Knobs[k] = v;
        return c;
    }

    private static LayoutFile Stamped(LayoutFile v2)
    {
        var v1 = WidgetExpander.Expand(v2, Repo.Shipped()).Layout;
        v1.Version = 1;
        v1.Widgets = v2.Copies!.ToDictionary(c => c.Id, c => new WidgetRecord { Template = c.Widget, Knobs = new(c.Knobs) });
        return LayoutFile.Parse(v1.ToJson());
    }

    private static LayoutFile Dials() => new()
    {
        Version = 2,
        BaseImage = "wall.jpg",
        Copies = [Copy("dial-1", "dial", 3000, 400, ("metric", "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu"), ("warnAt", "0.9")), Copy("dial-2", "dial", 3100, 400)],
    };

    private static MigrationResult Migrate(LayoutFile v1)
    {
        var r = LayoutMigrator.Migrate(v1, Repo.Shipped());
        Assert.True(r.Equivalent, string.Join(Environment.NewLine, r.Notes));
        Assert.True(LayoutMigrator.Migrate(r.V2, Repo.Shipped()).Equivalent);
        return r;
    }

    [Fact]
    public void An_Untouched_Instance_Keeps_Only_Its_Non_Default_Knobs()
    {
        var r = Migrate(Stamped(Dials()));
        var dial = r.V2.Copies!.Single(c => c.Id == "dial-1");
        Assert.Equal((3000, 400, 0), (dial.X, dial.Y, dial.Z));
        Assert.Equal(["metric"], dial.Knobs.Keys); // warnAt was at its default
        Assert.Empty(dial.Overrides);
        Assert.Empty(r.V2.Sources); // hardware now comes from the copies
    }

    [Fact]
    public void A_Drifted_Property_And_A_Moved_Part_Become_Overrides()
    {
        var v1 = Stamped(Dials());
        ((TextDef)v1.Components.Single(c => c.Id == "dial-2.label")).Color = PropertyValue.Literal("#FFFFC000");
        v1.Components.Single(c => c.Id == "dial-2.label").Rect = new Rect(3100, 470, 80, 16);
        v1.Components.Single(c => c.Id == "dial-2.value").Z = 5;

        var o = Migrate(v1).V2.Copies!.Single(c => c.Id == "dial-2").Overrides;
        Assert.Equal(new HashSet<string> { "components.value.z", "components.label.rect", "components.label.color" }, o.Keys.ToHashSet());
        Assert.Equal("#FFFFC000", o["components.label.color"].LiteralText);
        Assert.Equal("0,70,80,16", o["components.label.rect"].LiteralText);
    }

    [Fact]
    public void A_Deleted_Part_Is_Hidden_And_An_Extra_Component_Stays_Loose()
    {
        var v1 = Stamped(Dials());
        v1.Components.RemoveAll(c => c.Id == "dial-1.label");
        v1.Components.Add(new TextDef { Id = "dial-1.extra", Widget = "dial-1", Rect = new Rect(3000, 500, 80, 16), Text = PropertyValue.Literal("x") });

        var v2 = Migrate(v1).V2;
        Assert.Equal("true", v2.Copies!.Single(c => c.Id == "dial-1").Overrides["components.label.hidden"].LiteralText);
        var extra = Assert.Single(v2.Components);
        Assert.Equal(("dial-1.extra", null), (extra.Id, extra.Widget));
    }

    [Fact]
    public void A_Part_Whose_Type_Changed_Stays_Loose_And_The_Widget_Part_Is_Hidden()
    {
        var v1 = Stamped(Dials());
        var i = v1.Components.FindIndex(c => c.Id == "dial-2.label");
        v1.Components[i] = new ImageDef { Id = "dial-2.label", Widget = "dial-2", Rect = v1.Components[i].Rect, Z = v1.Components[i].Z, Source = PropertyValue.Literal("x.png") };

        var v2 = Migrate(v1).V2;
        Assert.Equal(["components.label.hidden"], v2.Copies!.Single(c => c.Id == "dial-2").Overrides.Keys);
        Assert.IsType<ImageDef>(Assert.Single(v2.Components));
    }

    [Fact]
    public void A_Difference_No_Override_Can_Express_Is_Reported_Not_Equivalent()
    {
        var v1 = Stamped(new LayoutFile { Version = 2, BaseImage = "wall.jpg", Copies = [Copy("headline-1", "headline", 0, 0)] });
        v1.Sources.Single().Settings.Remove("max");

        var r = LayoutMigrator.Migrate(v1, Repo.Shipped());
        Assert.False(r.Equivalent);
        Assert.Contains(r.Notes, n => n.StartsWith("not equivalent: ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Source_Renamed_By_A_V1_Clash_Maps_Back_To_The_Widget_Name()
    {
        // v1 stamped a weather instance next to a hand-placed "weather" of another type, so the
        // instance's source became "weather2" and its bindings followed.
        var v2 = new LayoutFile
        {
            Version = 2,
            BaseImage = "wall.jpg",
            Sources = [new SourceDef { Name = "weather", Type = "command", Settings = { ["command"] = "x.exe" } }],
            Components = [new TextDef { Id = "mine", Rect = new Rect(0, 0, 10, 10), Text = PropertyValue.Bound(Binding.Parse("weather.stdout")) }],
            Copies = [Copy("weather-1", "weather", 3000, 100, ("town", "York||53.9||-1.1"))],
        };
        var v1 = Stamped(v2);
        Assert.Contains(v1.Sources, s => s.Name == "weather2");
        var sources = v1.Sources.Single(s => s.Name == "weather2");
        sources.EverySeconds = 60; // and then drifted

        var r = Migrate(v1);
        var copy = Assert.Single(r.V2.Copies!);
        Assert.Equal("York||53.9||-1.1", copy.Knobs["town"]);
        Assert.Equal(["sources.weather.every"], copy.Overrides.Keys);
        Assert.Equal(["weather"], r.V2.Sources.Select(s => s.Name)); // the loose one; weather2 is the copy's again
    }
}
