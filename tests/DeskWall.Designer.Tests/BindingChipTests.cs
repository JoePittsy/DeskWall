using System.Collections.Generic;
using System.Linq;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
using Xunit;

/// <summary>Task 4.5: what the binding chip says and offers, without a window.</summary>
public class BindingChipTests
{
    private static readonly RecordValue Tree = new(new Dictionary<string, Value>
    {
        ["pc"] = new RecordValue(new Dictionary<string, Value>
        {
            ["cpu"] = new NumberValue(0.27),
            ["gpuName"] = new TextValue("RTX"),
        }),
        ["disks"] = new RecordValue(new Dictionary<string, Value>
        {
            ["drives"] = new ListValue([new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("C") })], "letter"),
        }),
    });

    // A hardware source named "pc": labels come from the source's type, not its name.
    private static readonly IReadOnlyList<ValueEntry> Entries = ValueCatalog.From(Tree,
        [new SourceDef { Name = "pc", Type = "hardware" }, new SourceDef { Name = "disks", Type = "disks" }]);

    [Fact]
    public void The_Chip_Reads_As_The_Label_And_The_Value_Now()
    {
        Assert.Equal("CPU load · 27%", BindingChip.Describe(Binding.Parse("pc.cpu | \"{0:0%}\""), Entries, Tree));
        Assert.Equal("pc.nothing · no value yet", BindingChip.Describe(Binding.Parse("pc.nothing"), Entries, Tree));
        Assert.Equal("Drives · 1 item", BindingChip.Describe(Binding.Parse("disks.drives"), Entries, Tree));
    }

    [Fact]
    public void Only_Values_That_Fit_The_Row_Are_Offered_And_Every_Word_Must_Match()
    {
        Assert.Equal(["pc.cpu"], BindingChip.Matching(Entries, PropertySchema.Editor.Number, "").Select(e => e.Path));
        Assert.Equal(["disks.drives"], BindingChip.Matching(Entries, PropertySchema.Editor.Binding, "").Select(e => e.Path));
        Assert.Equal(["pc.cpu"], BindingChip.Matching(Entries, PropertySchema.Editor.Text, "CPU load").Select(e => e.Path));
        Assert.Empty(BindingChip.Matching(Entries, PropertySchema.Editor.Text, "cpu banana"));
        Assert.Contains(BindingChip.Matching(Entries, PropertySchema.Editor.Text, "gpuname"), e => e.Path == "pc.gpuName");   // by path too
    }

    [Fact]
    public void A_Pick_Is_A_Binding_With_The_Preset_Format_And_Round_Trips_The_Path()
    {
        var cpu = Entries.Single(e => e.Path == "pc.cpu");
        var b = BindingChip.BindingFor(cpu, FormatPresets.For(cpu.Kind)[0].Format);
        Assert.Equal("pc.cpu | \"{0:0%}\"", b.ToString());
        Assert.Equal("pc.cpu", BindingChip.PathText(b));
        Assert.Equal("pc.cpu", BindingChip.BindingFor(cpu, null).ToString());
        Assert.Equal("disks.drives[C].letter", BindingChip.PathText(Binding.Parse("disks.drives[C].letter")));
    }
}
