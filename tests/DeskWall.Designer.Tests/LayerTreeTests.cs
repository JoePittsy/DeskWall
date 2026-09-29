using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 3.3: the Layers panel's rows, front first.</summary>
public class LayerTreeTests
{
    private static WidgetTemplate Dial() => new()
    {
        Name = "Hardware dial", Key = "dial", Description = "d", Width = 80, Height = 80,
        Components =
        [
            new DialDef { Id = "dial", Rect = new Rect(0, 0, 80, 80), Fraction = PropertyValue.Literal(0.5) },
            new TextDef { Id = "value", Rect = new Rect(0, 26, 80, 28), Text = PropertyValue.Literal("50%"), Z = 1 },
            new TextDef { Id = "label", Rect = new Rect(0, 62, 80, 16), Text = PropertyValue.Literal("cpu") },
        ],
    };

    private static WidgetTemplate Drives() => new()
    {
        Name = "Drives", Key = "drives", Description = "d", Width = 172, Height = 92,
        Components =
        [
            new RepeaterDef
            {
                Id = "drives", Rect = new Rect(0, 0, 172, 92), Items = PropertyValue.Literal(""),
                Template =
                [
                    new TextDef { Id = "letter", Rect = new Rect(0, 0, 60, 24), Text = PropertyValue.Literal("C:") },
                    new TextDef { Id = "free", Rect = new Rect(60, 0, 112, 24), Text = PropertyValue.Literal("1 GB") },
                    new BarDef { Id = "bar", Rect = new Rect(0, 26, 172, 6), Fraction = PropertyValue.Literal(0.5) },
                ],
            },
        ],
    };

    private static IReadOnlyList<LayerRow> Rows(string copies, string components = "", Func<string, bool>? forked = null)
    {
        var layout = LayoutFile.Parse($$"""
            { "version": 2, "baseImage": "x.jpg", "sources": [],
              "components": [ {{components}} ], "copies": [ {{copies}} ] }
            """);
        WidgetTemplate? Find(string key) => key switch
        {
            "dial" => Dial(),
            "drives" => Drives(),
            "corrupt" => throw new FormatException("corrupt.json: bad"),
            _ => null,
        };
        return LayerTree.Build(layout, WidgetExpander.Expand(layout, Find), Find, forked ?? (_ => false));
    }

    private static LayerRow Row(IReadOnlyList<LayerRow> rows, string key) => LayerTree.Flatten(rows).Single(r => r.Key == key);

    /// <summary>Critique 3, P2-b: four "Hardware dial" rows said nothing about which was which.</summary>
    [Fact]
    public void Copies_Of_One_Widget_Are_Named_By_The_Knob_That_Tells_Them_Apart()
    {
        var layout = LayoutFile.Parse("""
            { "version": 2, "baseImage": "x.jpg", "sources": [], "components": [],
              "copies": [
                { "id": "dial-1", "widget": "dial", "x": 0, "y": 0 },
                { "id": "dial-2", "widget": "dial", "x": 0, "y": 100, "knobs": { "metric": "GPU||hardware.gpu||x||gpu" } },
                { "id": "dial-3", "widget": "dial", "x": 0, "y": 200, "knobs": { "metric": "GPU||hardware.gpu||x||gpu", "warnAt": "0.5" } },
                { "id": "drives-1", "widget": "drives", "x": 0, "y": 300 } ] }
            """);
        WidgetTemplate? Find(string key) => key switch
        {
            "dial" => new WidgetTemplate
            {
                Name = "Hardware dial", Key = "dial", Description = "d", Width = 80, Height = 80, Components = Dial().Components,
                Knobs =
                [
                    new Knob("metric", "Metric", KnobType.Choice, "CPU||hardware.cpu||x||cpu", [], ["CPU||hardware.cpu||x||cpu", "GPU||hardware.gpu||x||gpu"], null, null),
                    new Knob("warnAt", "Warn at", KnobType.Number, "0.9", [], null, 0, 1),
                ],
            },
            "drives" => Drives(),
            _ => null,
        };
        var rows = LayerTree.Build(layout, WidgetExpander.Expand(layout, Find), Find, _ => false);
        Assert.Equal("Hardware dial · CPU", Row(rows, "dial-1").Name);
        Assert.Equal("Hardware dial · GPU", Row(rows, "dial-2").Name);
        Assert.Equal("Hardware dial · GPU", Row(rows, "dial-3").Name);   // Metric differs first, so Metric names them
        Assert.Equal("Drives", Row(rows, "drives-1").Name);                    // the only one of its widget
        Assert.Null(LayerTree.Distinguish([layout.Copies![0], layout.Copies[0]], layout.Copies[0], Find));   // nothing differs
    }

    [Fact]
    public void The_Override_Dot_Is_On_The_Overridden_Part_Only()
    {
        var rows = Rows("""{ "id": "dial-1", "widget": "dial", "x": 10, "y": 10, "overrides": { "components.label.color": "#FFFF8000" } }""");
        var copy = Assert.Single(rows);
        Assert.Equal(("Hardware dial", "dial-1", LayerKind.Copy), (copy.Name, copy.Detail, copy.Kind));
        Assert.True(copy.HasOverride);
        Assert.True(Row(rows, "dial-1.label").HasOverride);
        Assert.Equal("label", Row(rows, "dial-1.label").Name);
        Assert.False(Row(rows, "dial-1.value").HasOverride);
        Assert.False(Row(rows, "dial-1.dial").HasOverride);
        Assert.False(copy.IsOrphan || copy.IsBroken || copy.IsForkedShipped);
    }

    [Fact]
    public void An_Orphan_Override_Gets_Its_Own_Row_Under_The_Copy()
    {
        var rows = Rows("""{ "id": "dial-1", "widget": "dial", "x": 0, "y": 0, "overrides": { "components.gone.color": "#FF000000", "components.label.size": 20 } }""");
        var copy = Assert.Single(rows);
        Assert.True(copy.IsOrphan);
        var orphan = Assert.Single(copy.Children, r => r.Kind == LayerKind.Orphan);
        Assert.Equal(("components.gone.color", "override", "dial-1"), (orphan.Name, orphan.Detail, orphan.SelectId));
        Assert.True(orphan.IsOrphan);
        Assert.Equal(LayerKind.Orphan, copy.Children[^1].Kind);   // after the parts
        Assert.True(Row(rows, "dial-1.label").HasOverride);
        Assert.False(Row(rows, "dial-1.label").IsOrphan);
    }

    [Fact]
    public void A_Missing_Or_Broken_Widget_Gets_A_Row_And_Forks_Are_Flagged()
    {
        var rows = Rows("""
            { "id": "gone-1", "widget": "no-such", "x": 0, "y": 0 },
            { "id": "bad-1", "widget": "corrupt", "x": 0, "y": 0 },
            { "id": "dial-1", "widget": "dial", "x": 0, "y": 0 }
            """, forked: key => key == "dial");
        var gone = rows.Single(r => r.SelectId == "gone-1");
        Assert.True(gone.IsBroken);
        Assert.Equal("no-such", gone.Name);
        Assert.Empty(gone.Children);
        Assert.True(rows.Single(r => r.SelectId == "bad-1").IsBroken);
        Assert.False(gone.IsForkedShipped);
        Assert.True(rows.Single(r => r.SelectId == "dial-1").IsForkedShipped);
    }

    [Fact]
    public void Rows_Are_Front_First_In_Paint_Order()
    {
        // byhand z 5 is in front of everything; low z -1 behind; dial-2 is after dial-1 at equal z,
        // so it paints later and lists first; the broken copy's box is drawn over the canvas.
        var rows = Rows("""
            { "id": "dial-1", "widget": "dial", "x": 0, "y": 0 },
            { "id": "dial-2", "widget": "dial", "x": 0, "y": 100 },
            { "id": "gone-1", "widget": "no-such", "x": 0, "y": 0 }
            """, """
            { "type": "text", "id": "byhand", "rect": [0, 0, 10, 10], "text": "x", "z": 5 },
            { "type": "text", "id": "low", "rect": [0, 0, 10, 10], "text": "x", "z": -1 }
            """);
        Assert.Equal(["gone-1", "byhand", "dial-2", "dial-1", "low"], rows.Select(r => r.SelectId));
        // value has z 1, so it is the frontmost part; label follows dial in the file, so it is next.
        Assert.Equal(["value", "label", "dial"], rows.Single(r => r.SelectId == "dial-1").Children.Select(r => r.Name));
        Assert.Equal(LayerKind.Component, rows[1].Kind);
    }

    [Fact]
    public void Repeater_Children_Are_Rows_Under_Their_Repeater_With_Their_Own_Override_Dot()
    {
        var rows = Rows("""{ "id": "drives-1", "widget": "drives", "x": 0, "y": 0, "overrides": { "components.drives.free.size": 22 } }""",
            """{ "type": "repeater", "id": "list", "rect": [0, 0, 10, 10], "items": "", "template": [ { "type": "text", "id": "t", "rect": [0, 0, 5, 5], "text": "x" } ] }""");
        var repeater = Row(rows, "drives-1.drives");
        Assert.False(repeater.HasOverride);
        Assert.Equal(["bar", "free", "letter"], repeater.Children.Select(r => r.Name));
        var free = Row(rows, "drives-1.drives\u0000free");
        Assert.Equal((LayerKind.Child, "drives-1.drives", "free", "text"), (free.Kind, free.SelectId, free.ChildId, free.Detail));
        Assert.True(free.HasOverride);
        Assert.False(Row(rows, "drives-1.drives\u0000letter").HasOverride);

        var loose = Row(rows, "list\u0000t");
        Assert.Equal(("list", "t"), (loose.SelectId, loose.ChildId));
    }
}
