using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Goldens;

/// <summary>
/// Renders four fixed layouts through the real Direct2D path (Surface, FrameRenderer,
/// LayoutResolver - no shortcut through a mock) and compares the result, pixel for pixel, against
/// a checked-in PNG under Goldens/. This is the only test coverage that would catch a change to
/// how text, bars, images or repeaters actually paint; everything else in Render/ and Resolve/
/// tests geometry and content keys, never pixels.
/// <para>
/// Canvas is 860x360, a quarter of the real 3440x1440 wallpaper, so the goldens stay small. The
/// base is always a flat #FF203040 fill, generated at test time (not checked in - a solid colour
/// needs no binary asset). Every bound value in a layout comes from a literal ValueTree built in
/// this file, never a real source; the one thing that would otherwise be non-deterministic (the
/// clock) never appears bound to a real clock here.
/// </para>
/// <para>
/// To regenerate the goldens after a deliberate renderer change, run once with the environment
/// variable GOLDENS_UPDATE=1 set (for example: <c>GOLDENS_UPDATE=1 dotnet test --filter
/// FullyQualifiedName~GoldenTests</c>). That writes the four PNGs under the test output's Goldens
/// folder instead of comparing; copy them back over tests/DeskWall.Core.Tests/Goldens/*.png in the
/// source tree and commit with a message that says which renderer commit they were made from.
/// Never set GOLDENS_UPDATE in CI - a run with it set always "passes" without checking anything.
/// </para>
/// </summary>
public class GoldenTests
{
    private const int W = 860, H = 360;
    private const double MaxDifferentFraction = 0.001;   // 0.1 percent of the canvas

    private static readonly string GoldenDir = Path.Combine(AppContext.BaseDirectory, "Goldens");
    private static readonly string LayoutsDir = Path.Combine(GoldenDir, "layouts");
    private static bool Updating => Environment.GetEnvironmentVariable("GOLDENS_UPDATE") == "1";

    // The one checked-in image asset the goldens draw. Treated like a golden itself: only written
    // when missing or when GOLDENS_UPDATE=1, otherwise loaded as a fixed input. A 2:3 portrait with
    // banded colours and a translucent stripe so cover/contain/stretch and opacity all show visibly
    // different pixels rather than three copies of a flat fill.
    private static readonly string CoverImagePath = EnsureCoverImage();

    private static string EnsureCoverImage()
    {
        var path = Path.Combine(GoldenDir, "cover.png");
        if (File.Exists(path) && !Updating) return path;
        Directory.CreateDirectory(GoldenDir);
        using var s = Surface.Create(60, 90);
        s.Clear(Color.Parse("#FF8040C0"));
        s.FillRect(new Rect(0, 0, 60, 30), Color.Parse("#FFE0A030"));
        s.FillRect(new Rect(0, 30, 60, 30), Color.Parse("#FF30A0E0"));
        s.FillRect(new Rect(0, 60, 60, 30), Color.Parse("#FF30E070"));
        s.FillRect(new Rect(20, 0, 20, 90), Color.Parse("#A0FFFFFF"));
        s.SavePng(path);
        return path;
    }

    private static readonly Lazy<string> FlatBaseRawPath = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "golden-base");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "flat.png");
        using (var s = Surface.Create(W, H)) { s.Clear(Color.Parse("#FF203040")); s.SavePng(png); }
        return BaseCache.Ensure(png, W, H, Fit.Stretch);
    });

    private static void RunGolden(string name, RecordValue tree)
    {
        var layout = LayoutFile.Load(Path.Combine(LayoutsDir, name + ".json"));
        var resolved = LayoutResolver.Resolve(layout, tree);
        using var frame = new FrameRenderer(W, H).RenderAll(FlatBaseRawPath.Value, resolved);

        var goldenPath = Path.Combine(GoldenDir, name + ".png");
        if (Updating)
        {
            frame.SavePng(goldenPath);
            return;
        }

        Assert.True(File.Exists(goldenPath), $"golden missing: {goldenPath}. Run with GOLDENS_UPDATE=1 to create it.");
        using var golden = Surface.Load(goldenPath);
        var (different, bounds) = Compare.Diff(frame, golden);
        var total = W * H;
        if (different > total * MaxDifferentFraction)
        {
            frame.SavePng(Path.Combine(GoldenDir, name + ".actual.png"));
            Compare.WriteDiffPng(frame, golden, Path.Combine(GoldenDir, name + ".diff.png"));
            Assert.Fail($"{name}: {different} of {total} pixels differ by more than tolerance (bounds {bounds}); " +
                        $"see {name}.actual.png and {name}.diff.png next to the golden");
        }
    }

    [Fact]
    public void Text_Styles()
    {
        // All four effects (none, shadow, outline, plate) as rows; three alignments (left, center,
        // right) and three sizes (20, 30, 42) tied together as columns.
        RunGolden("text-styles", ValueTree.Empty);
    }

    [Fact]
    public void Bar_States()
    {
        // Zero, half, and a fraction at/above the threshold (switches fill colour), plus one
        // vertical bar. All literal fractions: no source in the loop.
        RunGolden("bar-states", ValueTree.Empty);
    }

    [Fact]
    public void Image_Fits()
    {
        var tree = ValueTree.Of(("img", new RecordValue(new Dictionary<string, Value>
        {
            ["cover"] = new TextValue(CoverImagePath),
        })));
        // cover/contain/stretch across columns; radius 0 (row 1, opacity 1) vs radius 12 and
        // opacity 0.5 (row 2) across rows.
        RunGolden("image-fits", tree);
    }

    [Fact]
    public void Repeater_Auto()
    {
        RecordValue Item(int i) => new(new Dictionary<string, Value>
        {
            ["name"] = new TextValue("item" + i),
            ["cover"] = new TextValue(CoverImagePath),
            ["label"] = new TextValue("Item " + i),
        });
        var list = new ListValue([Item(1), Item(2), Item(3)], "name");
        var tree = ValueTree.Of(("games", new RecordValue(new Dictionary<string, Value> { ["list"] = list })));
        // Three items; cellHeight "auto" takes the row height from the cover image's real aspect
        // ratio (60x90), decoded through the same Surface.Load path the renderer uses, not a
        // hardcoded number.
        RunGolden("repeater-auto", tree);
    }
}
