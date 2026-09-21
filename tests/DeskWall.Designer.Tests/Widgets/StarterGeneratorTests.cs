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
    private static string RepoRoot() => TestRepo.Root;

    private static IReadOnlyList<WidgetTemplate> Catalog() => TestRepo.Widgets();

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
