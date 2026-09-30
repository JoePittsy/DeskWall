using System.IO;
using System.Linq;
using System.Threading;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Tests.Widgets;
using Xunit;

/// <summary>Phase 6 Task 6: every layout shipped in layouts/ opens in the designer exactly as
/// MainWindow opens one (a DesignerModel on the authored file, on the ultrawide canvas it was
/// authored for) and previews with every copy expanded and nothing skipped.</summary>
public class RepoLayoutsTests
{
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(TestRepo.Root, "layouts"), "*.json").Order())
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Opens_And_Previews_In_The_Designer(string fileName)
    {
        var path = Path.Combine(TestRepo.Root, "layouts", fileName);
        var layout = LayoutFile.Load(path);
        Assert.True(layout.Version <= LayoutStore.MaxVersion, $"{fileName} is version {layout.Version}");
        layout.BaseImage = FlatBase();   // the Spotlight asset is a property of the machine, not the layout

        var model = new DesignerModel(layout, new DisplaySignature("TEST", 3440, 1440, 100), path);
        using var renderer = new PreviewRenderer(() => ValueTree.Empty);
        PreviewFrame? frame = null;
        using var done = new ManualResetEventSlim();
        renderer.Rendered += f => { frame = f; done.Set(); };
        renderer.Request(model, null);
        Assert.True(done.Wait(20_000), "no frame arrived");

        Assert.Empty(frame!.Problems);
        Assert.NotEmpty(frame.Resolved);
        Assert.Equal(3440, frame.Width);
    }

    private static string FlatBase()
    {
        var path = Path.Combine(Paths.RuntimeDir, "repo-layouts-base.png");
        if (!File.Exists(path))
        {
            using var s = Surface.Create(4, 4);
            s.Clear(new Color(255, 40, 60, 80));
            s.SavePng(path);
        }
        return path;
    }
}
