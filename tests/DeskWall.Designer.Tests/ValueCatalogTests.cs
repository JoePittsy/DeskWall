using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 4.1: the live value tree flattened into (path, label, kind, sample).</summary>
public class ValueCatalogTests
{
    private static RecordValue Rec(params (string Name, Value Value)[] fields) => ValueTree.Of(fields);

    private static SourceDef Def(string name, string type) => new() { Name = name, Type = type };

    private static ValueEntry Entry(IReadOnlyList<ValueEntry> all, string path) => Assert.Single(all, e => e.Path == path);

    [Fact]
    public void Hardware_Fractions_Are_Fractions_And_Percents_Are_Numbers_With_Labels()
    {
        var tree = Rec(("hardware", Rec(("cpu", new NumberValue(0.274)), ("cpuPct", new NumberValue(27)), ("samples", new NumberValue(1)))));
        var hw = Def("hardware", "hardware");
        var all = ValueCatalog.From(tree, [hw]);

        var cpu = Entry(all, "hardware.cpu");
        Assert.Equal(("CPU load", ValueKind.Fraction), (cpu.Label, cpu.Kind));
        Assert.Same(hw, cpu.Source);
        Assert.Equal(new NumberValue(0.274), cpu.Sample);
        Assert.Equal(("CPU load %", ValueKind.Number), (Entry(all, "hardware.cpuPct").Label, Entry(all, "hardware.cpuPct").Kind));
        // 1 is inside 0..1, but samples is a count: the kind comes from the docs, not the sample.
        Assert.Equal(ValueKind.Number, Entry(all, "hardware.samples").Kind);
    }

    [Fact]
    public void A_Keyed_List_Gives_The_List_And_Each_Item_By_Key()
    {
        var drive = Rec(("letter", new TextValue("C")), ("freeGB", new NumberValue(64)), ("usedFraction", new NumberValue(0.8)));
        var tree = Rec(("disks", Rec(("drives", new ListValue([drive], "letter")))));
        var all = ValueCatalog.From(tree, [Def("disks", "disks")]);

        Assert.Equal(ValueKind.List, Entry(all, "disks.drives").Kind);
        var used = Entry(all, "disks.drives[C].usedFraction");
        Assert.Equal(("C: used", ValueKind.Fraction), (used.Label, used.Kind));
        Assert.Equal(("C: free GB", ValueKind.Number), (Entry(all, "disks.drives[C].freeGB").Label, Entry(all, "disks.drives[C].freeGB").Kind));
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void Time_Bool_And_Text_Kinds()
    {
        var tree = Rec(
            ("time", Rec(("now", new TimeValue(DateTimeOffset.Now)), ("weekday", new TextValue("Monday")))),
            ("system", Rec(("pendingReboot", new BoolValue(false)), ("machine", new TextValue("JOES-PC")))));
        var all = ValueCatalog.From(tree, [Def("time", "time"), Def("system", "system")]);

        Assert.Equal(("Time", ValueKind.Timestamp), (Entry(all, "time.now").Label, Entry(all, "time.now").Kind));
        Assert.Equal(("Reboot pending", ValueKind.Bool), (Entry(all, "system.pendingReboot").Label, Entry(all, "system.pendingReboot").Kind));
        Assert.Equal(ValueKind.Text, Entry(all, "system.machine").Kind);
        Assert.Equal(ValueKind.Text, Entry(all, "time.weekday").Kind);
    }

    [Fact]
    public void Labels_Follow_The_Source_Type_Not_Its_Name()
    {
        var tree = Rec(("pc", Rec(("ram", new NumberValue(0.5)))));
        var ram = Entry(ValueCatalog.From(tree, [Def("pc", "hardware")]), "pc.ram");
        Assert.Equal(("RAM used", ValueKind.Fraction), (ram.Label, ram.Kind));
    }

    [Fact]
    public void A_Pushed_Provider_Falls_Back_To_The_Path_And_Has_No_Source()
    {
        var tree = Rec(("build", Rec(("data", Rec(("level", new NumberValue(0.4)))))));
        var level = Entry(ValueCatalog.From(tree, []), "build.data.level");
        Assert.Equal(("build.data.level", ValueKind.Number), (level.Label, level.Kind));
        Assert.Null(level.Source);
    }

    /// <summary>The table is keyed on field names from docs/sources.md; this is what catches a
    /// source renaming a field under it.</summary>
    [Fact]
    public async Task Every_Field_The_Real_Time_And_Disks_Sources_Publish_Has_A_Label()
    {
        // Sun settings, so sunrise/sunset are published and need labels too.
        var time = await new TimeSource("time", SystemClock.Instance, "07:00", "19:00").RefreshAsync(CancellationToken.None);
        var disks = await new DisksSource("disks", TimeSpan.FromMinutes(5)).RefreshAsync(CancellationToken.None);
        var all = ValueCatalog.From(Rec(("time", time), ("disks", disks)), [Def("time", "time"), Def("disks", "disks")]);

        Assert.Contains(all, e => e.Path.StartsWith("disks.drives[", StringComparison.Ordinal));
        Assert.All(all, e => Assert.NotEqual(e.Path, e.Label));
        Assert.Equal(ValueKind.Fraction, Entry(all, "time.dayFraction").Kind);
        Assert.Equal(ValueKind.Number, Entry(all, "time.dayPercent").Kind);
        Assert.Equal(ValueKind.Fraction, Entry(all, "time.sunFraction").Kind);
        Assert.Equal(ValueKind.Fraction, Entry(all, "time.nightFraction").Kind);
        Assert.Equal(ValueKind.Fraction, Entry(all, "time.skyFraction").Kind);
        Assert.Equal(ValueKind.Timestamp, Entry(all, "time.sunset").Kind);
    }

    [Theory]
    [InlineData(PropertySchema.Editor.Number, ValueKind.Fraction, true)]
    [InlineData(PropertySchema.Editor.Number, ValueKind.Number, true)]
    [InlineData(PropertySchema.Editor.Number, ValueKind.Text, false)]
    [InlineData(PropertySchema.Editor.Number, ValueKind.Timestamp, false)]
    [InlineData(PropertySchema.Editor.AutoNumber, ValueKind.Number, true)]
    [InlineData(PropertySchema.Editor.Text, ValueKind.Timestamp, true)]
    [InlineData(PropertySchema.Editor.Text, ValueKind.Bool, true)]
    [InlineData(PropertySchema.Editor.Text, ValueKind.List, false)]
    [InlineData(PropertySchema.Editor.Color, ValueKind.Bool, true)]
    [InlineData(PropertySchema.Editor.Path, ValueKind.Text, true)]
    [InlineData(PropertySchema.Editor.Binding, ValueKind.List, true)]
    [InlineData(PropertySchema.Editor.Binding, ValueKind.Text, false)]
    public void Fits(PropertySchema.Editor editor, ValueKind kind, bool fits) => Assert.Equal(fits, ValueCatalog.Fits(editor, kind));
    /// <summary>Critique 2, P2: the Data list hides values about the source (sample counts, raw bytes
    /// and seconds) until they are searched for, so the rows that fit are the ones worth dragging.</summary>
    [Fact]
    public void Diagnostic_Values_Show_In_The_Data_List_Only_When_Searched_For()
    {
        var tree = Rec(("hardware", Rec(("cpu", new NumberValue(0.27)), ("samples", new NumberValue(6)), ("window", new NumberValue(60)))),
            ("system", Rec(("uptime", new NumberValue(3600)), ("uptimeText", new TextValue("1h")))));
        var all = ValueCatalog.From(tree, [Def("hardware", "hardware"), Def("system", "system")]);
        var rows = all.Select(e => new DeskWall.Designer.Views.InsertPanel.DataRow(e)).ToList();

        Assert.Equal(["hardware.cpu", "system.uptimeText"], rows.Where(r => r.Matches("")).Select(r => r.Path));
        Assert.Equal(["hardware.samples"], rows.Where(r => r.Matches("samples")).Select(r => r.Path));
        Assert.True(Entry(all, "system.uptime").Diagnostic);
        Assert.False(Entry(all, "hardware.cpu").Diagnostic);
    }

    /// <summary>Critique 3, P3: "Drives · [2 items]" and "Days since crash · -1" in the Data list.</summary>
    [Fact]
    public void The_Data_List_Never_Shows_A_List_Itself_And_Says_No_Crash_In_Words()
    {
        var drive = Rec(("letter", new TextValue("C")), ("freeGB", new NumberValue(463)));
        var tree = Rec(("disks", Rec(("drives", new ListValue([drive], "letter")))),
            ("system", Rec(("daysSinceCrash", new NumberValue(-1)))));
        var all = ValueCatalog.From(tree, [Def("disks", "disks"), Def("system", "system")]);
        var rows = all.Select(e => new DeskWall.Designer.Views.InsertPanel.DataRow(e)).ToList();

        Assert.Contains(all, e => e.Kind == ValueKind.List);   // still there for a repeater's items
        Assert.DoesNotContain(rows, r => r.Entry.Kind == ValueKind.List && (r.Matches("") || r.Matches("drives")));
        Assert.Contains(rows, r => r.Path == "disks.drives[C].freeGB" && r.Matches(""));
        Assert.Equal("no crash on record", DeskWall.Designer.Views.InsertPanel.DataRow.SampleText(Entry(all, "system.daysSinceCrash")));
        Assert.Equal("3", DeskWall.Designer.Views.InsertPanel.DataRow.SampleText(Entry(all, "system.daysSinceCrash") with { Sample = new NumberValue(3) }));
    }
}
