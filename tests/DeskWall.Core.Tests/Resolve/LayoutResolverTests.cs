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
        var r = LayoutResolver.Resolve(Layout(), Tree(), new Rect(0, 0, 3440, 1440));
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
        var r = LayoutResolver.Resolve(Layout(), Tree(), new Rect(0, 0, 3440, 1440));
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
        var r = LayoutResolver.Resolve(Layout(), Tree(), new Rect(0, 0, 3440, 1440));
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
        var r = LayoutResolver.Resolve(layout, Tree(), new Rect(0, 0, 3440, 1440));
        Assert.Equal("100", ((ResolvedText)r.Single(c => c.Id == "toomany")).Text);
        Assert.Equal("100", ((ResolvedText)r.Single(c => c.Id == "unbalanced")).Text);
    }
}
