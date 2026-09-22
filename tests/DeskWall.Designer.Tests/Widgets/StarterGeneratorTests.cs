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
    [InlineData("steam-recent.json")]
    public void Committed_Starter_Equals_Generated(string fileName)
    {
        var catalog = Catalog();
        var generated = Generate(fileName, catalog);
        var committed = LayoutFile.Load(Path.Combine(RepoRoot(), "layouts", fileName));
        Assert.Equal(generated.ToJson(), committed.ToJson());
    }

    /// <summary>The thing this plan is really about: every starter is <em>defined</em> from
    /// widgets, so no component in one belongs to nothing. A loose component is invisible to the
    /// gallery's count, to the Arranger and to the knobs panel, which is how steam-recent.json sat
    /// hand-authored without anybody noticing. (Repeater children are not checked: they belong to
    /// their repeater, which belongs to the instance.)</summary>
    [Theory]
    [InlineData("column-system.json")]
    [InlineData("clock-disks.json")]
    [InlineData("steam-recent.json")]
    public void No_Component_In_A_Starter_Belongs_To_No_Widget(string fileName)
    {
        var committed = LayoutFile.Load(Path.Combine(RepoRoot(), "layouts", fileName));
        Assert.NotEmpty(committed.Components);
        Assert.Empty(committed.Components.Where(c => string.IsNullOrEmpty(c.Widget)).Select(c => c.Id));
        Assert.NotNull(committed.Widgets);
        foreach (var c in committed.Components) Assert.Contains(c.Widget!, committed.Widgets!.Keys);
    }

    private static LayoutFile Generate(string fileName, IReadOnlyList<WidgetTemplate> catalog) => fileName switch
    {
        "column-system.json" => StarterGenerator.ColumnSystem(catalog),
        "clock-disks.json" => StarterGenerator.ClockDisks(catalog),
        "steam-recent.json" => StarterGenerator.SteamRecent(catalog),
        _ => throw new ArgumentOutOfRangeException(nameof(fileName)),
    };
}
