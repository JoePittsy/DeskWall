using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

public class LayoutResolverTests
{
    private static RecordValue Tree()
    {
        RecordValue Drive(string l, double frac) => new(new Dictionary<string, Value> { ["letter"] = new TextValue(l), ["freeGB"] = new NumberValue(100), ["usedFraction"] = new NumberValue(frac) });
        var disks = new RecordValue(new Dictionary<string, Value> { ["drives"] = new ListValue([Drive("C", 0.5), Drive("D", 0.9), Drive("E", 0.1)], "letter") });
        var time = new RecordValue(new Dictionary<string, Value> { ["now"] = new TimeValue(new DateTimeOffset(2026, 9, 20, 14, 32, 0, TimeSpan.Zero)) });
        return ValueTree.Of(("disks", disks), ("time", time));
    }

    private static LayoutFile Layout() => LayoutFile.Parse("""
    {
      "version": 1, "baseImage": "x.jpg", "sources": [],
      "components": [
        { "type": "text", "id": "clock", "rect": [100, 10, 172, 70], "z": 1, "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "align": "right" },
        { "type": "repeater", "id": "drives", "rect": [100, 200, 172, 92], "items": { "bind": "disks.drives" }, "axis": "vertical", "gap": 0, "cellHeight": 46,
          "template": [
            { "type": "text", "id": "letter", "rect": [0, 0, 60, 24], "text": { "bind": "letter | \"{0}:\"" } },
            { "type": "bar", "id": "bar", "rect": [0, 26, 172, 6], "fraction": { "bind": "usedFraction" }, "threshold": 0.85, "thresholdFill": "#FFD13438" },
            { "type": "shortcut", "id": "go", "rect": [0, 0, 172, 46], "slot": 10, "target": { "bind": "letter | \"explorer.exe {0}:\\\"" } }
          ] },
        { "type": "text", "id": "missing", "rect": [0, 0, 10, 10], "text": { "bind": "nope.value" } },
        { "type": "shortcut", "id": "nolink", "rect": [0, 0, 10, 10], "target": { "bind": "nope.value" } }
      ]
    }
    """);

    [Fact]
    public void Resolves_Text_With_Style()
    {
        var r = LayoutResolver.Resolve(Layout(), Tree());
        var clock = Assert.IsType<ResolvedText>(r.Single(c => c.Id == "clock"));
        Assert.Equal("14:32", clock.Text);
        Assert.Equal("Segoe UI Light", clock.Style.Font);
        Assert.Equal(64f, clock.Style.Size);
        Assert.Equal(Align.Right, clock.Style.Align);
        Assert.Equal(16, clock.ContentKey.Length);
    }

    [Fact]
    public void Expands_Repeater_Within_Bounds_And_Assigns_Slots()
    {
        var r = LayoutResolver.Resolve(Layout(), Tree());
        // 92 px tall, 46 px cells: only 2 of 3 drives fit
        var letters = r.OfType<ResolvedText>().Where(t => t.Id.StartsWith("drives[")).ToList();
        Assert.Equal(["C:", "D:"], letters.Select(t => t.Text));
        Assert.Equal(new Rect(100, 246, 60, 24), letters[1].Rect);
        var bars = r.OfType<ResolvedBar>().ToList();
        Assert.Equal(Color.Parse("#EBFFFFFF"), bars[0].Fill);   // 0.5 < 0.85: normal fill
        Assert.Equal(Color.Parse("#FFD13438"), bars[1].Fill);   // 0.9 >= 0.85: threshold fill
        var scs = r.OfType<ResolvedShortcut>().ToList();
        Assert.Equal([10, 11], scs.Select(s => s.Slot));
        Assert.Equal(@"explorer.exe D:\", scs[1].Target);
    }

    [Fact]
    public void Missing_Bindings_Fallback()
    {
        var r = LayoutResolver.Resolve(Layout(), Tree());
        Assert.Equal("", ((ResolvedText)r.Single(c => c.Id == "missing")).Text);
        Assert.DoesNotContain(r, c => c.Id == "nolink");
    }

    /// <summary>Finding 5: a malformed format string in a binding must render a fallback, not abort
    /// the tick with a FormatException out of resolve.</summary>
    [Fact]
    public void Malformed_Format_String_Does_Not_Abort_Resolve()
    {
        var layout = LayoutFile.Parse("""
        {
          "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "text", "id": "toomany", "rect": [0, 0, 100, 20], "text": { "bind": "disks.drives[C].freeGB | \"{0:N0} GB, {1} total\"" } },
            { "type": "text", "id": "unbalanced", "rect": [0, 30, 100, 20], "text": { "bind": "disks.drives[C].freeGB | \"{0:N0} GB {free\"" } }
          ]
        }
        """);
        var r = LayoutResolver.Resolve(layout, Tree());
        Assert.Equal("100", ((ResolvedText)r.Single(c => c.Id == "toomany")).Text);
        Assert.Equal("100", ((ResolvedText)r.Single(c => c.Id == "unbalanced")).Text);
    }

    /// <summary>Finding 6: a duplicate id used to reach TickRunner's ToDictionary after the
    /// wallpaper had already been applied. Resolve must reject it first.</summary>
    [Fact]
    public void Duplicate_Component_Ids_Throw_Before_Anything_Is_Drawn()
    {
        var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [], "components": [
          { "type": "text", "id": "a", "rect": [0, 0, 10, 10], "text": "x" },
          { "type": "text", "id": "a", "rect": [20, 0, 10, 10], "text": "y" }
        ] }
        """);
        var ex = Assert.Throws<InvalidOperationException>(() => LayoutResolver.Resolve(layout, ValueTree.Empty));
        Assert.Contains("a", ex.Message);
    }

    [Fact]
    public void Dial_Resolves_Defaults_And_Clamps()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "dial", "id": "d", "rect": [10, 10, 80, 80], "fraction": 1.7 } ] }
            """);
        var d = Assert.IsType<ResolvedDial>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
        Assert.Equal(1.0, d.Fraction);
        Assert.Equal(Color.Parse("#46FFFFFF"), d.Track);
        // A clamped 1.7 is exactly at the default threshold of 1, so the fill is the threshold
        // colour, as it is for a full bar. The default fill is covered by the fraction-0 test below.
        Assert.Equal(Color.Parse("#D13438"), d.Fill);
        Assert.Equal(6f, d.Thickness);
        Assert.Equal(225f, d.StartAngle);
        Assert.Equal(270f, d.Sweep);
    }

    [Fact]
    public void Dial_At_Or_Above_Threshold_Uses_ThresholdFill()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "dial", "id": "d", "rect": [0, 0, 50, 50], "fraction": 0.9, "threshold": 0.9, "thresholdFill": "#FF112233" } ] }
            """);
        var d = Assert.IsType<ResolvedDial>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
        Assert.Equal(Color.Parse("#FF112233"), d.Fill);
    }

    [Fact]
    public void Dial_Bound_Fraction_Missing_Falls_Back_To_Zero()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "dial", "id": "d", "rect": [0, 0, 50, 50], "fraction": { "bind": "hw.cpu" } } ] }
            """);
        var d = Assert.IsType<ResolvedDial>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
        Assert.Equal(0.0, d.Fraction);
        Assert.Equal(Color.Parse("#EBFFFFFF"), d.Fill);   // below the default threshold: the default fill
    }

    [Fact]
    public void Image_Source_Runtime_Prefix_Resolves_Into_The_Runtime_Dir()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "image", "id": "i", "rect": [0, 0, 10, 10],
                                "source": { "bind": "w.code | \"runtime:assets/weather/{0}.png\"" } } ] }
            """);
        var tree = ValueTree.Of(("w", new RecordValue(new Dictionary<string, Value> { ["code"] = new NumberValue(61) })));
        var img = Assert.IsType<ResolvedImage>(Assert.Single(LayoutResolver.Resolve(layout, tree)));
        Assert.Equal(Paths.InRuntime("assets", "weather", "61.png"), img.Path);
    }

    [Fact]
    public void Image_Source_Without_Prefix_Is_Unchanged()
    {
        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [ { "type": "image", "id": "i", "rect": [0, 0, 10, 10], "source": "C:\\pics\\a.png" } ] }
            """);
        var img = Assert.IsType<ResolvedImage>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
        Assert.Equal(@"C:\pics\a.png", img.Path);
    }

    /// <summary>Fix round 1: the repeater's "auto" cell height measures the first image child
    /// through its own read of <c>img.Source</c>, which did not expand "runtime:". The file was
    /// therefore never found and every runtime-dir cover fell back to the 2:3 placeholder aspect.</summary>
    [Fact]
    public void Auto_Cell_Height_Measures_A_Runtime_Image_Source()
    {
        var path = Paths.InRuntime("test-assets", "wide.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var s = Surface.Create(40, 10)) s.SavePng(path);   // 4:1, nothing like the 2:3 placeholder

        var layout = LayoutFile.Parse("""
            { "version": 1, "baseImage": "x.jpg", "sources": [],
              "components": [
                { "type": "repeater", "id": "r", "rect": [0, 0, 200, 400], "items": { "bind": "d.items" },
                  "axis": "vertical", "cellHeight": "auto",
                  "template": [ { "type": "image", "id": "i", "rect": [0, 0, 80, 20], "source": "runtime:test-assets/wide.png" } ] } ] }
            """);
        var item = new RecordValue(new Dictionary<string, Value> { ["name"] = new TextValue("a") });
        var tree = ValueTree.Of(("d", new RecordValue(new Dictionary<string, Value> { ["items"] = new ListValue([item], "name") })));

        var img = Assert.IsType<ResolvedImage>(Assert.Single(LayoutResolver.Resolve(layout, tree)));
        Assert.Equal(path, img.Path);
        Assert.Equal(20, img.Rect.H);   // 80 * 10 / 40; the 2:3 placeholder would have said 120
    }
}

/// <summary>Finding 4: template children used to escape their cell on the main axis (the cell was
/// sized from the auto image alone) and were unbounded on the cross axis.</summary>
public class RepeaterOverflowTests
{
    private static RecordValue Items(int n)
    {
        var rows = new List<RecordValue>();
        for (var i = 0; i < n; i++)
            rows.Add(new RecordValue(new Dictionary<string, Value>
            {
                ["name"] = new TextValue("row" + i),
                ["cover"] = new TextValue(Path.Combine(Path.GetTempPath(), "deskwall-tests", "no-such-cover.png")),
            }));
        return ValueTree.Of(("games", new RecordValue(new Dictionary<string, Value> { ["list"] = new ListValue(rows, "name") })));
    }

    [Fact]
    public void Vertical_Cell_Covers_Every_Child_And_Cross_Axis_Is_Clamped()
    {
        var layout = LayoutFile.Parse("""
        {
          "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "repeater", "id": "g", "rect": [0, 1000, 100, 340], "items": { "bind": "games.list" }, "axis": "vertical", "cellHeight": "auto",
              "template": [
                { "type": "image", "id": "cover", "rect": [0, 0, 100, 150], "source": { "bind": "cover" } },
                { "type": "text", "id": "name", "rect": [0, 150, 400, 20], "text": { "bind": "name" } }
              ] }
          ]
        }
        """);
        var r = LayoutResolver.Resolve(layout, Items(3));

        // auto image extent is 150 (100 wide at the 2:3 placeholder aspect), but the name child ends
        // at 170, so the cell is 170: two of three rows fit in 340 px, not three.
        var names = r.OfType<ResolvedText>().ToList();
        Assert.Equal(2, names.Count);
        Assert.Equal(1150, names[0].Rect.Y);
        Assert.Equal(1320, names[1].Rect.Y);
        Assert.Equal(1170, r.OfType<ResolvedImage>().ElementAt(1).Rect.Y);   // row 1 starts after the whole cell

        // every child stays inside the repeater's 100 px width and inside its own cell
        Assert.All(names, t => Assert.Equal(100, t.Rect.W));
        Assert.All(r, c => Assert.True(c.Rect.Right <= 100 && c.Rect.Bottom <= 1340, $"{c.Id} escapes the block: {c.Rect}"));

        // widening the cell to fit the name must not stretch the cover out of its aspect ratio
        Assert.All(r.OfType<ResolvedImage>(), i => Assert.Equal(150, i.Rect.H));
    }

    [Fact]
    public void Horizontal_Cell_Covers_Every_Child_And_Cross_Axis_Is_Clamped()
    {
        var layout = LayoutFile.Parse("""
        {
          "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "repeater", "id": "g", "rect": [0, 0, 400, 80], "items": { "bind": "games.list" }, "axis": "horizontal", "cellHeight": 100,
              "template": [
                { "type": "text", "id": "a", "rect": [0, 0, 60, 20], "text": { "bind": "name" } },
                { "type": "text", "id": "b", "rect": [120, 0, 60, 20], "text": { "bind": "name" } },
                { "type": "text", "id": "c", "rect": [0, 0, 60, 200], "text": { "bind": "name" } }
              ] }
          ]
        }
        """);
        var r = LayoutResolver.Resolve(layout, Items(3));

        // declared cell 100, but child "b" ends at 180, so the cell is 180: two of three rows fit in 400 px.
        Assert.Equal(180, r.Single(c => c.Id == "g[1].a").Rect.X);
        Assert.DoesNotContain(r, c => c.Id.StartsWith("g[2]."));
        Assert.Equal(300, r.Single(c => c.Id == "g[1].b").Rect.X);

        // child "c" is 200 px tall in an 80 px block: clamped, not painted over whatever is below
        Assert.Equal(80, r.Single(c => c.Id == "g[0].c").Rect.H);
        Assert.All(r, c => Assert.True(c.Rect.Right <= 400 && c.Rect.Bottom <= 80, $"{c.Id} escapes the block: {c.Rect}"));
    }
}

public class ContentKeyTests
{
    [Fact]
    public void Key_Changes_With_Content_Not_With_Identity()
    {
        var a = new ResolvedText("x", new Rect(0, 0, 10, 10), 0, "14:32", TextStyle.Default);
        var b = new ResolvedText("y", new Rect(0, 0, 10, 10), 0, "14:32", TextStyle.Default);
        var c = new ResolvedText("x", new Rect(0, 0, 10, 10), 0, "14:33", TextStyle.Default);
        var d = new ResolvedText("x", new Rect(1, 0, 10, 10), 0, "14:32", TextStyle.Default);
        Assert.Equal(ContentKey.Of(a), ContentKey.Of(b));
        Assert.NotEqual(ContentKey.Of(a), ContentKey.Of(c));
        Assert.NotEqual(ContentKey.Of(a), ContentKey.Of(d));
    }
}
