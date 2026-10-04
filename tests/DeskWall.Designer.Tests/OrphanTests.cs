using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>#78 and #80: what a widget-depth edit kills goes with it, in the same undo entry; what
/// arrived dead from outside is listed on the copy and removed on request. Uses "dial" (its
/// composite Metric knob sets the dial, the value and the label; Warn at sets the dial only).</summary>
public class OrphanTests
{
    private static DesignerModel TwoDials(string dial1 = "") => DesignerModelDepthTests.Model($$"""
        { "id": "dial-1", "widget": "dial", "x": 100, "y": 50 {{dial1}} },
        { "id": "dial-2", "widget": "dial", "x": 300, "y": 50 }
        """);

    private static WidgetCopy Copy(DesignerModel m, string id) => m.Layout.Copies!.Single(c => c.Id == id);

    private static Knob Knob(WidgetTemplate t, string id) => t.Knobs.Single(k => k.Id == id);

    private static IEnumerable<ExpandProblem> Orphans(DesignerModel m)
        => m.Expanded().Problems.Where(p => p.Kind is ExpandProblemKind.OrphanKnob or ExpandProblemKind.OrphanOverride);

    /// <summary>The shipped dial without its "dial" part, knobs untouched: what a hand edit, or a
    /// newer file on disk, leaves.</summary>
    private static WidgetTemplate WithoutDialPart(WidgetTemplate t) => new()
    {
        Name = t.Name, Key = t.Key, Description = t.Description, Width = t.Width, Height = t.Height,
        Anchor = t.Anchor, Sources = t.Sources, Knobs = t.Knobs,
        Components = LayoutFile.Parse(new LayoutFile { BaseImage = "", Components = [.. t.Components] }.ToJson()).Components.Where(c => c.Id != "dial").ToList(),
    };

    [Fact]
    public void Deleting_A_Part_At_Widget_Depth_Drops_A_Knob_That_Set_Only_It_And_Trims_One_That_Set_More()
    {
        var m = TwoDials();
        m.SetDepth(Depth.Widget("dial", "dial-1"));
        m.Remove(["dial-1.dial"]);

        var edited = m.WidgetEdits["dial"];
        Assert.Equal(["metric"], edited.Knobs.Select(k => k.Id));
        var metric = Knob(edited, "metric");
        Assert.Equal(["components.value.text=bind:hardware.cpuPct | \"{0}%\"", "components.label.text"], metric.Sets);
        // The composite loses the part the dial took, so value and label still get theirs.
        Assert.Equal("CPU||hardware.cpuPct | \"{0}%\"||cpu", metric.Default);
        Assert.Equal("GPU||hardware.gpuPct | \"{0}%\"||gpu", metric.Choices![1]);
        Assert.Empty(Orphans(m));
        Assert.Equal("cpu", ((TextDef)m.Expanded().Layout.Components.Single(c => c.Id == "dial-2.label")).Text.LiteralText);
    }

    [Fact]
    public void The_Copies_Lose_Their_Values_For_What_Went_And_Keep_An_Orphan_That_Was_Already_There()
    {
        var m = TwoDials("""
            , "knobs": { "warnAt": "0.5", "metric": "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu" },
              "overrides": { "components.dial.threshold": 0.7, "components.label.size": 20, "components.gone.color": "#FF000000" }
            """);
        string? said = null;
        m.Notice += s => said = s;
        m.SetDepth(Depth.Widget("dial", "dial-1"));
        m.Remove(["dial-1.dial"]);

        var copy = Copy(m, "dial-1");
        Assert.Equal(["metric"], copy.Knobs.Keys);
        Assert.Equal("GPU||hardware.gpuPct | \"{0}%\"||gpu", copy.Knobs["metric"]);
        Assert.Equal(["components.gone.color", "components.label.size"], copy.Overrides.Keys.Order());
        Assert.Equal(["components.gone.color"], Orphans(m).Select(p => p.Detail));
        Assert.Equal("gpu", ((TextDef)m.Expanded().Layout.Components.Single(c => c.Id == "dial-1.label")).Text.LiteralText);
        Assert.NotNull(said);
        Assert.Contains("Warn at", said, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Z", said, StringComparison.Ordinal);
    }

    [Fact]
    public void One_Undo_Brings_Back_The_Part_The_Knobs_And_The_Copies_Values()
    {
        var m = TwoDials("""
            , "knobs": { "warnAt": "0.5" }, "overrides": { "components.dial.threshold": 0.7 }
            """);
        var layout = m.Layout.ToJson();
        m.SetDepth(Depth.Widget("dial", "dial-1"));
        m.Remove(["dial-1.dial"]);
        m.Undo();

        Assert.Equal(layout, m.Layout.ToJson());
        Assert.Empty(m.WidgetEdits);
        Assert.Equal(["metric", "warnAt"], m.Finder()("dial")!.Knobs.Select(k => k.Id));
        Assert.Empty(Orphans(m));
    }

    [Fact]
    public void An_Edit_That_Kills_Nothing_Leaves_The_Knobs_Alone_And_Says_Nothing()
    {
        var m = TwoDials();
        string? said = null;
        m.Notice += s => said = s;
        m.SetDepth(Depth.Widget("dial", "dial-1"));
        m.Move(["dial-1.label"], 0, 2);
        Assert.Equal(["metric", "warnAt"], m.WidgetEdits["dial"].Knobs.Select(k => k.Id));
        Assert.Null(said);
    }

    [Fact]
    public void Remove_Takes_An_Orphan_Override_Off_The_Copy_As_One_Undo_Entry()
    {
        var m = TwoDials("""
            , "overrides": { "components.gone.color": "#FF000000", "components.label.size": 20 }
            """);
        Assert.True(Lens.RemoveOrphan(m, "dial-1", "components.gone.color", knob: false));
        Assert.Equal(["components.label.size"], Copy(m, "dial-1").Overrides.Keys);
        Assert.Empty(Orphans(m));
        Assert.DoesNotContain(LayerTree.Flatten(LayerTree.Build(m)), r => r.Kind == LayerKind.Orphan);

        m.Undo();
        Assert.Equal(2, Copy(m, "dial-1").Overrides.Count);
        Assert.False(Lens.RemoveOrphan(m, "dial-1", "components.label.size", knob: false));   // live, not an orphan
    }

    [Fact]
    public void Remove_Takes_A_Value_For_A_Knob_The_Widget_No_Longer_Has_Off_The_Copy()
    {
        var m = TwoDials("""
            , "knobs": { "gone": "1", "warnAt": "0.5" }
            """);
        Assert.Equal(["gone"], Orphans(m).Select(p => p.Detail));
        Assert.True(Lens.RemoveOrphan(m, "dial-1", "gone", knob: true));
        Assert.Equal(["warnAt"], Copy(m, "dial-1").Knobs.Keys);
        Assert.Empty(Orphans(m));
        Assert.Empty(m.WidgetEdits);
    }

    [Fact]
    public void Remove_On_A_Knob_The_Widget_Itself_Left_Dead_Trims_It_From_The_Widget_For_Every_Copy()
    {
        var m = TwoDials("""
            , "knobs": { "warnAt": "0.5", "metric": "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu" }
            """);
        m.Edit("Hand edit", (_, edits) => edits["dial"] = WithoutDialPart(m.Finder()("dial")!));
        Assert.Equal(["dial-1:metric", "dial-1:warnAt", "dial-2:metric", "dial-2:warnAt"], Orphans(m).Select(p => $"{p.CopyId}:{p.Detail}").Order());

        Assert.True(Lens.RemoveOrphan(m, "dial-2", "warnAt", knob: true));
        Assert.Equal(["metric"], m.WidgetEdits["dial"].Knobs.Select(k => k.Id));
        Assert.Equal(["metric"], Copy(m, "dial-1").Knobs.Keys);
        Assert.Equal(["dial-1:metric", "dial-2:metric"], Orphans(m).Select(p => $"{p.CopyId}:{p.Detail}").Order());

        Assert.True(Lens.RemoveOrphan(m, "dial-1", "metric", knob: true));
        Assert.Equal("GPU||hardware.gpuPct | \"{0}%\"||gpu", Copy(m, "dial-1").Knobs["metric"]);
        Assert.Empty(Orphans(m));

        m.Undo();
        m.Undo();
        Assert.Equal(4, Orphans(m).Count());
    }
}
