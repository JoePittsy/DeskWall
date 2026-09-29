using DeskWall.Core.Layout;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using DeskWall.Core.Widgets;
using Xunit;

/// <summary>Every layout file shipped under layouts/ loads, expands against the repo's widgets/
/// with no problem (they are v2 copies), and resolves against an empty tree without throwing (a
/// missing binding is a default, never an exception - spec 3.2).</summary>
public class StarterLayoutTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    private static string RepoLayouts() => Path.Combine(RepoRoot(), "layouts");

    public static IEnumerable<object[]> Files() =>
        Directory.EnumerateFiles(RepoLayouts(), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>What the daemon resolves: the file expanded before resolve (plan D4).</summary>
    private static LayoutFile Expanded(string file)
    {
        var x = WidgetExpander.Expand(LayoutFile.Load(Path.Combine(RepoLayouts(), file)), WidgetCatalog.Finder(Path.Combine(RepoRoot(), "widgets")));
        Assert.Empty(x.Problems);
        return x.Layout;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Shipped_Layout_Loads_Expands_And_Resolves_Empty(string file)
    {
        var resolved = LayoutResolver.Resolve(Expanded(file), ValueTree.Empty);
        Assert.NotEmpty(resolved);
    }

    [Fact]
    public void Column_System_Uses_Every_New_Source_And_A_Dial()
    {
        var layout = Expanded("column-system.json");
        Assert.Contains(layout.Sources, s => s.Type == "hardware");
        Assert.Contains(layout.Sources, s => s.Type == "http" && s.Name == "weather");
        Assert.Contains(layout.Sources, s => s.Type == "command" && s.Name == "tailscale");
        Assert.Contains(layout.Components, c => c is DialDef);
        Assert.NotEmpty(LayoutResolver.Resolve(layout, ValueTree.Empty));
    }
}
