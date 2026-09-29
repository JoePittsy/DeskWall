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
        var time = await new TimeSource("time", SystemClock.Instance).RefreshAsync(CancellationToken.None);
        var disks = await new DisksSource("disks", TimeSpan.FromMinutes(5)).RefreshAsync(CancellationToken.None);
        var all = ValueCatalog.From(Rec(("time", time), ("disks", disks)), [Def("time", "time"), Def("disks", "disks")]);

        Assert.Contains(all, e => e.Path.StartsWith("disks.drives[", StringComparison.Ordinal));
        Assert.All(all, e => Assert.NotEqual(e.Path, e.Label));
        Assert.Equal(ValueKind.Fraction, Entry(all, "time.dayFraction").Kind);
        Assert.Equal(ValueKind.Number, Entry(all, "time.dayPercent").Kind);
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
}
