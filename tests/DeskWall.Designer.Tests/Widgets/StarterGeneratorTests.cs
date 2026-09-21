using System.IO;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>layouts/column-system.json and layouts/clock-disks.json are generated from the
/// shipped widgets, not hand-placed (plan step 4); this asserts the committed files still equal
/// what StarterGenerator produces, so the two cannot drift apart silently.</summary>
public class StarterGeneratorTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    private static IReadOnlyList<WidgetTemplate> Catalog() => WidgetCatalog.Load(Path.Combine(RepoRoot(), "widgets"));

    [Theory]
    [InlineData("column-system.json")]
    [InlineData("clock-disks.json")]
    public void Committed_Starter_Equals_Generated(string fileName)
    {
        var catalog = Catalog();
        var generated = fileName == "column-system.json" ? StarterGenerator.ColumnSystem(catalog) : StarterGenerator.ClockDisks(catalog);
        var committed = LayoutFile.Load(Path.Combine(RepoRoot(), "layouts", fileName));
        Assert.Equal(generated.ToJson(), committed.ToJson());
    }
}
