using DeskWall.Core.Layout;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

/// <summary>Every layout file shipped under layouts/ loads, and resolves against an empty tree
/// without throwing (a missing binding is a default, never an exception - spec 3.2).</summary>
public class StarterLayoutTests
{
    private static string RepoLayouts()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return Path.Combine(d.FullName, "layouts");
        throw new InvalidOperationException("repo root not found");
    }

    public static IEnumerable<object[]> Files() =>
        Directory.EnumerateFiles(RepoLayouts(), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Files))]
    public void Shipped_Layout_Loads_And_Resolves_Empty(string file)
    {
        var layout = LayoutFile.Load(Path.Combine(RepoLayouts(), file));
        var resolved = LayoutResolver.Resolve(layout, ValueTree.Empty);
        Assert.NotNull(resolved);
    }

    [Fact]
    public void Column_System_Uses_Every_New_Source_And_A_Dial()
    {
        var layout = LayoutFile.Load(Path.Combine(RepoLayouts(), "column-system.json"));
        Assert.Contains(layout.Sources, s => s.Type == "hardware");
        Assert.Contains(layout.Sources, s => s.Type == "http" && s.Name == "weather");
        Assert.Contains(layout.Sources, s => s.Type == "command" && s.Name == "tailscale");
        Assert.Contains(layout.Components, c => c is DialDef);
        Assert.NotEmpty(LayoutResolver.Resolve(layout, ValueTree.Empty));
    }
}
