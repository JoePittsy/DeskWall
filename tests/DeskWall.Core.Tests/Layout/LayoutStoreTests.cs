using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using Xunit;

public class LayoutStoreTests
{
    private static (LayoutStore store, string dir) Fresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (new LayoutStore(Path.Combine(dir, "layouts.json")), dir);
    }

    private static string WriteLayout(string dir, string name, int w)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, $$$"""{ "version": 1, "baseImage": "x.jpg", "sources": [], "components": [ { "type": "text", "id": "t", "rect": [{{{w - 100}}}, 0, 100, 50], "text": "x" } ] }""");
        return p;
    }

    [Fact]
    public void Empty_Store_Resolves_Null()
    {
        var (store, _) = Fresh();
        Assert.Null(store.Resolve(new DisplaySignature("A", 3440, 1440, 100)));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Exact_Match_Is_Not_Scaled_And_Persists()
    {
        var (store, dir) = Fresh();
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        var path = WriteLayout(dir, "a.json", 3440);
        store.Set(sig, path);
        var again = new LayoutStore(Path.Combine(dir, "layouts.json"));
        var r = again.Resolve(sig)!;
        Assert.False(r.Scaled);
        Assert.Equal(path, r.SourcePath);
        Assert.Equal(new Rect(3340, 0, 100, 50), r.Layout.Components[0].Rect);
        Assert.Contains(path, again.WatchPaths);
        Assert.Contains(Path.Combine(dir, "layouts.json"), again.WatchPaths);
    }

    [Fact]
    public void Unknown_Signature_Picks_Closest_And_Scales()
    {
        var (store, dir) = Fresh();
        var ultrawide = new DisplaySignature("A", 3440, 1440, 100);
        var laptop = new DisplaySignature("B", 1920, 1080, 100);
        store.Set(ultrawide, WriteLayout(dir, "uw.json", 3440));
        store.Set(laptop, WriteLayout(dir, "lap.json", 1920));
        // same device A at a new resolution: device match beats aspect match
        var r = store.Resolve(new DisplaySignature("A", 1720, 720, 100))!;
        Assert.True(r.Scaled);
        Assert.Equal(ultrawide, r.SourceSignature);
        Assert.Equal(new Rect(1670, 0, 50, 25), r.Layout.Components[0].Rect);
        // unknown device, 16:9: aspect match picks the laptop layout
        var r2 = store.Resolve(new DisplaySignature("C", 3840, 2160, 150))!;
        Assert.Equal(laptop, r2.SourceSignature);
        Assert.Equal(new Rect(3640, 0, 200, 100), r2.Layout.Components[0].Rect);
    }

    [Fact]
    public void Remove_And_Missing_File_Are_Handled()
    {
        var (store, dir) = Fresh();
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        var path = WriteLayout(dir, "a.json", 3440);
        store.Set(sig, path);
        File.Delete(path);
        Assert.Null(store.Resolve(sig));   // entry exists but file is gone: treated as absent, not thrown
        store.Remove(sig);
        Assert.Empty(store.Entries);
    }
}
