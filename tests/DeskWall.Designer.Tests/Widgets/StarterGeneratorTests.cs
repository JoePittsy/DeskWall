using System.IO;
using DeskWall.Core.Layout;
using Xunit;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>layouts/*.json are generated from the shipped widgets as v2 copies, not hand-placed;
/// this asserts the committed files still equal what StarterGenerator produces, so the two cannot
/// drift apart silently, and that they paint exactly what the v1 stamped starters they replace
/// painted (those are kept as fixtures in tests/fixtures/layouts-v1/).</summary>
public class StarterGeneratorTests
{
    private static string RepoRoot() => TestRepo.Root;

    private static IReadOnlyList<WidgetTemplate> Catalog() => TestRepo.Widgets();

    private static Func<string, WidgetTemplate?> Find() => WidgetCatalog.Finder(TestRepo.WidgetsDir);

    private static LayoutFile Committed(string fileName) => LayoutFile.Load(Path.Combine(RepoRoot(), "layouts", fileName));

    private static LayoutFile V1(string fileName) => LayoutFile.Load(Path.Combine(RepoRoot(), "tests", "fixtures", "layouts-v1", fileName));

    private static string ComponentsJson(LayoutFile l) => new LayoutFile { BaseImage = "", Components = l.Components }.ToJson();

    private static string SourcesJson(LayoutFile l) => new LayoutFile { BaseImage = "", Sources = l.Sources }.ToJson();

    [Theory]
    [InlineData("column-system.json")]
    [InlineData("clock-disks.json")]
    [InlineData("steam-recent.json")]
    public void Committed_Starter_Equals_Generated(string fileName)
    {
        var generated = Generate(fileName, Catalog());
        // DESKWALL_REGEN_STARTERS=1 dotnet test --filter Committed_Starter_Equals_Generated rewrites
        // the committed files from the generator (after a widget change); review the diff.
        if (Environment.GetEnvironmentVariable("DESKWALL_REGEN_STARTERS") == "1")
            generated.Save(Path.Combine(RepoRoot(), "layouts", fileName));
        Assert.Equal(generated.ToJson(), Committed(fileName).ToJson());
    }

    /// <summary>Every starter is <em>defined</em> from widgets: version 2, nothing loose, no source
    /// of its own (each copy brings its widget's), and every copy expands with no problem.</summary>
    [Theory]
    [InlineData("column-system.json")]
    [InlineData("clock-disks.json")]
    [InlineData("steam-recent.json")]
    public void A_Starter_Is_Only_Linked_Copies(string fileName)
    {
        var committed = Committed(fileName);
        Assert.Equal(2, committed.Version);
        Assert.Empty(committed.Components);
        Assert.Empty(committed.Sources);
        Assert.Null(committed.Widgets);
        Assert.NotEmpty(committed.Copies!);
        Assert.Empty(WidgetExpander.Expand(committed, Find()).Problems);
    }

    /// <summary>The regeneration changes the file, not the wallpaper: the v2 starter expands to the
    /// v1 starter's components and sources, byte for byte, and migrating the v1 file gives the same
    /// copies the generator wrote (what `deskwall migrate --check` would print for either).</summary>
    [Theory]
    [InlineData("column-system.json")]
    [InlineData("clock-disks.json")]
    [InlineData("steam-recent.json")]
    public void The_V2_Starter_Paints_What_The_V1_Starter_Painted(string fileName)
    {
        var v1 = V1(fileName);
        var expanded = WidgetExpander.Expand(Committed(fileName), Find()).Layout;
        Assert.Equal(ComponentsJson(v1), ComponentsJson(expanded));
        Assert.Equal(SourcesJson(v1), SourcesJson(expanded));

        var migrated = LayoutMigrator.Migrate(v1, Find());
        Assert.True(migrated.Equivalent, string.Join(Environment.NewLine, migrated.Notes));
        Assert.Equal(Committed(fileName).ToJson(), migrated.V2.ToJson());
    }

    private static LayoutFile Generate(string fileName, IReadOnlyList<WidgetTemplate> catalog) => fileName switch
    {
        "column-system.json" => StarterGenerator.ColumnSystem(catalog),
        "clock-disks.json" => StarterGenerator.ClockDisks(catalog),
        "steam-recent.json" => StarterGenerator.SteamRecent(catalog),
        _ => throw new ArgumentOutOfRangeException(nameof(fileName)),
    };
}
