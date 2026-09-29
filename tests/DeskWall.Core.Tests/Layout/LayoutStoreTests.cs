using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
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

    /// <summary>Finding 16: version was parsed and never checked. A layout written by a future build
    /// must be refused and reported, not half-read into a frame the user cannot explain.</summary>
    [Fact]
    public void A_Layout_From_A_Future_Version_Is_Refused_And_Reported()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = new LayoutStore(Path.Combine(dir, "layouts.json"), errors.Add);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        var path = Path.Combine(dir, "v3.json");
        File.WriteAllText(path, """{ "version": 3, "baseImage": "x.jpg", "sources": [], "components": [] }""");
        store.Set(sig, path);

        Assert.Null(store.Resolve(sig));                       // exact match, but unreadable
        Assert.Null(store.Resolve(new DisplaySignature("B", 1920, 1080, 100)));   // and not a scaling candidate either
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("is version 3", e));
        Assert.All(errors, e => Assert.Contains($"up to {LayoutStore.MaxVersion}", e));
    }

    /// <summary>Finding 4: the owner has both his monitor and his Apollo session registered. When he
    /// saves a syntax error into the layout for the display he is looking at, the daemon must keep the
    /// last good wallpaper and say so - not silently paint the other display's layout scaled up.</summary>
    [Fact]
    public void A_Broken_Layout_For_This_Display_Does_Not_Fall_Back_To_Another()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = new LayoutStore(Path.Combine(dir, "layouts.json"), errors.Add);
        var desk = new DisplaySignature("A", 3440, 1440, 100);
        var apollo = new DisplaySignature("B", 1920, 1080, 100);
        store.Set(apollo, WriteLayout(dir, "apollo.json", 1920));   // readable, and otherwise a fine candidate
        var broken = Path.Combine(dir, "desk.json");
        File.WriteAllText(broken, """{ "version": 1, "baseImage": """);   // truncated
        store.Set(desk, broken);

        Assert.Null(store.Resolve(desk));
        Assert.Single(errors);
        Assert.Contains("desk.json", errors[0]);

        errors.Clear();
        File.Delete(broken);
        Assert.Null(store.Resolve(desk));           // missing is the same answer as unparseable
        Assert.Single(errors);
        Assert.Contains("is missing", errors[0]);
    }

    /// <summary>The other half of finding 4: closest-match still applies when this signature has no
    /// entry of its own, which is the case spec 5's scaling rules exist for.</summary>
    [Fact]
    public void A_Signature_With_No_Entry_Still_Gets_The_Closest_Match()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = new LayoutStore(Path.Combine(dir, "layouts.json"), errors.Add);
        var apollo = new DisplaySignature("B", 1920, 1080, 100);
        store.Set(apollo, WriteLayout(dir, "apollo.json", 1920));
        var broken = Path.Combine(dir, "desk.json");
        File.WriteAllText(broken, """{ "version": 1, "baseImage": """);
        store.Set(new DisplaySignature("A", 3440, 1440, 100), broken);

        var r = store.Resolve(new DisplaySignature("C", 3840, 2160, 150));
        Assert.NotNull(r);
        Assert.True(r!.Scaled);
        Assert.Equal(apollo, r.SourceSignature);   // the broken entry for another signature is skipped
        Assert.All(errors, e => Assert.Contains("desk.json", e));
    }

    [Fact]
    public void A_Version_1_Layout_Is_Still_Accepted_And_Reports_Nothing()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = new LayoutStore(Path.Combine(dir, "layouts.json"), errors.Add);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        store.Set(sig, WriteLayout(dir, "ok.json", 3440));
        Assert.NotNull(store.Resolve(sig));
        Assert.Empty(errors);
    }

    /// <summary>The daemon holds one store for its whole life; `deskwall layouts set` writes the file
    /// from a different process, so the running daemon only sees it after Reload.</summary>
    [Fact]
    public void Reload_Picks_Up_An_Entry_Written_By_Another_Process()
    {
        var (store, dir) = Fresh();
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        var other = new LayoutStore(Path.Combine(dir, "layouts.json"));
        other.Set(sig, WriteLayout(dir, "late.json", 3440));

        Assert.Null(store.Resolve(sig));
        store.Reload();
        Assert.NotNull(store.Resolve(sig));
    }

    // ---- v2 (plan Task 1.5). The store takes a fake finder, so none of this reads a widget file.
    // Tests marked Skip need WidgetExpander.Expand's body from lane/p1-expander (the seam throws for
    // a layout with copies); un-skip them once both lanes are merged.

    private const string NeedsExpander = "needs lane/p1-expander";

    /// <summary>A one-part widget: a text part "value" at (0,0) 80x20, with a time source.</summary>
    private static WidgetTemplate Widget(string key)
    {
        var parts = LayoutFile.Parse("""
            { "baseImage": "x.jpg",
              "sources": [ { "name": "time", "type": "time" } ],
              "components": [ { "type": "text", "id": "value", "rect": [0, 0, 80, 20], "text": "x" } ] }
            """);
        return new WidgetTemplate
        {
            Key = key, Name = key, Description = "test", Width = 80, Height = 20,
            Sources = parts.Sources, Components = parts.Components,
        };
    }

    private static string WriteV2(string dir, string name, string copies)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, $$$"""
            { "version": 2, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "text", "id": "t", "rect": [3340, 0, 100, 50], "text": "x" } ],
              "copies": [ {{{copies}}} ] }
            """);
        return p;
    }

    private static LayoutStore Store(string dir, List<string> errors, Func<string, WidgetTemplate?> find)
        => new(Path.Combine(dir, "layouts.json"), errors.Add, find);

    private static Func<string, WidgetTemplate?> NoLookups => key => throw new InvalidOperationException($"looked up widget {key}");

    [Fact]
    public void MaxVersion_Is_2() => Assert.Equal(2, LayoutStore.MaxVersion);

    [Fact]
    public void A_V1_Layout_Never_Looks_Up_A_Widget_And_Watches_No_Widget_Path()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, NoLookups);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        store.Set(sig, WriteLayout(dir, "a.json", 3440));
        Assert.NotNull(store.Resolve(sig));
        Assert.Empty(errors);
        Assert.Equal(2, store.WatchPaths.Count);   // layouts.json and a.json, nothing under widgets\
    }

    [Fact]
    public void A_V2_Layout_With_No_Copies_Is_Accepted_As_Itself()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, NoLookups);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        var path = Path.Combine(dir, "v2.json");
        File.WriteAllText(path, """{ "version": 2, "baseImage": "x.jpg", "sources": [], "components": [ { "type": "text", "id": "t", "rect": [3340, 0, 100, 50], "text": "x" } ] }""");
        store.Set(sig, path);
        var r = store.Resolve(sig)!;
        Assert.Empty(errors);
        Assert.Equal(new Rect(3340, 0, 100, 50), Assert.Single(r.Layout.Components).Rect);
    }

    [Fact(Skip = NeedsExpander)]
    public void A_V2_Layout_Resolves_Expanded()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, key => key == "x" ? Widget("x") : null);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        store.Set(sig, WriteV2(dir, "v2.json", """{ "id": "x-1", "widget": "x", "x": 3300, "y": 100 }"""));

        var r = store.Resolve(sig)!;
        Assert.Empty(errors);
        Assert.True(r.Layout.Copies is null or { Count: 0 });
        Assert.Equal(new Rect(3300, 100, 80, 20), Assert.Single(r.Layout.Components, c => c.Id == "x-1.value").Rect);
        Assert.Contains(r.Layout.Components, c => c.Id == "t");
        Assert.Contains(r.Layout.Sources, s => s.Name == "time");
    }

    [Fact(Skip = NeedsExpander)]
    public void A_V2_Layout_Is_Expanded_Before_It_Is_Scaled()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, key => key == "x" ? Widget("x") : null);
        store.Set(new DisplaySignature("A", 3440, 1440, 100), WriteV2(dir, "v2.json", """{ "id": "x-1", "widget": "x", "x": 3300, "y": 100 }"""));

        // Same device at half the resolution: the copy's parts scale like any other component.
        var r = store.Resolve(new DisplaySignature("A", 1720, 720, 100))!;
        Assert.True(r.Scaled);
        Assert.Empty(errors);
        Assert.Equal(new Rect(1650, 50, 40, 10), Assert.Single(r.Layout.Components, c => c.Id == "x-1.value").Rect);
    }

    /// <summary>Runs today: the keys are taken from the copies before expansion, so the watch set does
    /// not depend on the expander succeeding (the seam's Expand throws for copies, and the exception
    /// is deliberately ignored here; once lane/p1-expander lands it does not throw at all).</summary>
    [Fact]
    public void WatchPaths_Include_The_User_Dir_Path_Of_Every_Referenced_Widget()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, Widget);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        store.Set(sig, WriteV2(dir, "v2.json", """{ "id": "x-1", "widget": "x", "x": 0, "y": 0 }, { "id": "y-1", "widget": "y", "x": 0, "y": 40 }"""));

        Assert.DoesNotContain(Path.Combine(WidgetCatalog.UserDir, "x.json"), store.WatchPaths);   // not until a resolve referenced it
        _ = Record.Exception(() => store.Resolve(sig));
        Assert.Contains(Path.Combine(WidgetCatalog.UserDir, "x.json"), store.WatchPaths);
        Assert.Contains(Path.Combine(WidgetCatalog.UserDir, "y.json"), store.WatchPaths);
    }

    [Fact(Skip = NeedsExpander)]
    public void A_Missing_Widget_Is_Reported_Watched_And_The_Rest_Still_Paints()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, key => key == "x" ? Widget("x") : null);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        store.Set(sig, WriteV2(dir, "v2.json", """{ "id": "x-1", "widget": "x", "x": 0, "y": 0 }, { "id": "gone-1", "widget": "gone", "x": 0, "y": 40 }"""));

        var r = store.Resolve(sig)!;
        Assert.Contains(r.Layout.Components, c => c.Id == "x-1.value");
        Assert.Contains(r.Layout.Components, c => c.Id == "t");
        Assert.DoesNotContain(r.Layout.Components, c => c.Id.StartsWith("gone-1.", StringComparison.Ordinal));
        var e = Assert.Single(errors);
        Assert.Contains("gone-1", e);
        Assert.Contains("'gone'", e);
        // Watched even though missing, so creating the widget file repaints.
        Assert.Contains(Path.Combine(WidgetCatalog.UserDir, "gone.json"), store.WatchPaths);
    }

    [Fact(Skip = NeedsExpander)]
    public void A_Broken_Widget_Is_Reported_And_The_Rest_Still_Paints()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, key => key == "bad" ? throw new FormatException("bad.json: size must be [w, h]") : Widget(key));
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        store.Set(sig, WriteV2(dir, "v2.json", """{ "id": "x-1", "widget": "x", "x": 0, "y": 0 }, { "id": "bad-1", "widget": "bad", "x": 0, "y": 40 }"""));

        var r = store.Resolve(sig)!;
        Assert.Contains(r.Layout.Components, c => c.Id == "x-1.value");
        var e = Assert.Single(errors);
        Assert.Contains("bad-1", e);
        Assert.Contains("cannot be loaded", e);
    }

    [Fact(Skip = NeedsExpander)]
    public void An_Orphan_Override_Is_Not_Logged()
    {
        var (_, dir) = Fresh();
        var errors = new List<string>();
        var store = Store(dir, errors, Widget);
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        store.Set(sig, WriteV2(dir, "v2.json", """{ "id": "x-1", "widget": "x", "x": 0, "y": 0, "overrides": { "components.nope.color": "#FF000000" } }"""));

        Assert.NotNull(store.Resolve(sig));
        Assert.Empty(errors);
    }
}
