using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using Xunit;

/// <summary>Task 4.1: what dropping a live value offers, and that applying it is one undo entry that
/// adds the value's source once. Copies use "dial", never "clock" (see DesignerModelDepthTests).</summary>
public class DropPlanTests
{
    private static readonly SourceDef Hardware = new() { Name = "hardware", Type = "hardware", EverySeconds = 30 };
    private static readonly SourceDef Time = new() { Name = "time", Type = "time" };

    private static readonly ValueEntry Cpu = new("hardware.cpu", "CPU load", ValueKind.Fraction, new NumberValue(0.27), Hardware);
    private static readonly ValueEntry Ram = new("hardware.ram", "RAM used", ValueKind.Fraction, new NumberValue(0.5), Hardware);
    private static readonly ValueEntry RamGb = new("hardware.ramUsedGB", "RAM used GB", ValueKind.Number, new NumberValue(12.3), Hardware);
    private static readonly ValueEntry Now = new("time.now", "Time", ValueKind.Timestamp, new TimeValue(DateTimeOffset.Now), Time);

    /// <summary>A v2 layout with no copies, one text part and whatever sources are given.</summary>
    private static DesignerModel Plain(string sources = "") => new(LayoutFile.Parse($$"""
        { "version": 2, "baseImage": "x.jpg", "sources": [ {{sources}} ],
          "components": [ { "type": "text", "id": "label", "rect": [10, 10, 100, 20], "text": "hi" } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    private static string Bind(PropertyValue v) => Assert.IsType<DeskWall.Core.Bindings.Binding>(v.Binding).ToString();

    private static void OneUndoEntry(DesignerModel m, string before)
    {
        m.Undo();
        Assert.False(m.CanUndo);
        Assert.Equal(before, m.ToJson());
    }

    [Fact]
    public void A_Fraction_On_Empty_Canvas_Offers_Dial_And_Bar_And_A_Dial_Lands_Centred_And_Bound()
    {
        var m = Plain();
        var before = m.ToJson();
        var plan = DropPlan.For(m, Cpu, new DropTarget(500, 300));
        Assert.Equal(["Dial", "Bar"], plan.Options.Take(2).Select(o => o.Label));
        Assert.IsType<DialDef>(plan.Options[0].Create);
        Assert.IsType<BarDef>(plan.Options[1].Create);
        Assert.Contains(plan.Options, o => o.Label == "Text 27%");

        var id = plan.Apply(m, plan.Options[0]);
        var dial = Assert.IsType<DialDef>(m.Find(id));
        Assert.Equal(new Rect(460, 260, 80, 80), dial.Rect);
        Assert.Equal("hardware.cpu", Bind(dial.Fraction));
        var added = Assert.Single(m.Layout.Sources);
        Assert.Equal(("hardware", "hardware", 30), (added.Name, added.Type, added.EverySeconds));
        Assert.NotSame(Hardware, added);
        OneUndoEntry(m, before);
    }

    [Fact]
    public void A_Timestamp_On_Empty_Canvas_Offers_A_Clock_First()
    {
        var m = Plain();
        var plan = DropPlan.For(m, Now, new DropTarget(500, 300));
        Assert.Equal("Clock", plan.Options[0].Label);
        var clock = Assert.IsType<TextDef>(m.Find(plan.Apply(m, plan.Options[0])));
        Assert.Equal("time.now | \"HH:mm\"", Bind(clock.Text));
    }

    [Fact]
    public void A_Timestamp_On_A_Text_Part_Binds_Text_With_HH_mm()
    {
        var m = Plain();
        var before = m.ToJson();
        var plan = DropPlan.For(m, Now, new DropTarget(20, 15, "label"));
        Assert.All(plan.Options, o => Assert.Equal(("Text", null), (o.Property, o.Create)));
        Assert.Equal("HH:mm", plan.Options[0].Value.Binding!.Format);

        Assert.Equal("label", plan.Apply(m, plan.Options[0]));
        Assert.Equal("time.now | \"HH:mm\"", Bind(((TextDef)m.Find("label")!).Text));
        Assert.Equal("time", Assert.Single(m.Layout.Sources).Name);
        Assert.Single(m.Layout.Components);
        OneUndoEntry(m, before);
    }

    [Fact]
    public void The_Source_Is_Added_Once_However_Many_Values_Come_From_It()
    {
        var m = Plain();
        foreach (var v in new[] { Cpu, Ram, Cpu })
        {
            var plan = DropPlan.For(m, v, new DropTarget(500, 300));
            plan.Apply(m, plan.Options[0]);
        }
        Assert.Equal(3, m.Layout.Components.OfType<DialDef>().Count());
        Assert.Equal(["dial", "dial-2", "dial-3"], m.Layout.Components.OfType<DialDef>().Select(d => d.Id));
        Assert.Single(m.Layout.Sources);
    }

    [Fact]
    public void A_Source_Already_In_The_Layout_Is_Left_Alone()
    {
        var m = Plain("""{ "name": "hardware", "type": "hardware", "every": 60 }""");
        var plan = DropPlan.For(m, Cpu, new DropTarget(500, 300));
        plan.Apply(m, plan.Options[0]);
        Assert.Equal(60, Assert.Single(m.Layout.Sources).EverySeconds);
    }

    [Fact]
    public void A_Pushed_Provider_Adds_No_Source()
    {
        var m = Plain();
        var level = new ValueEntry("build.data.level", "build.data.level", ValueKind.Number, new NumberValue(4), null);
        var plan = DropPlan.For(m, level, new DropTarget(20, 15, "label"));
        plan.Apply(m, plan.Options[0]);
        Assert.Equal("build.data.level | \"{0:0}\"", Bind(((TextDef)m.Find("label")!).Text));
        Assert.Empty(m.Layout.Sources);
    }

    [Fact]
    public void A_Number_On_A_Dial_Does_Not_Bind_Its_Fraction()
    {
        var m = Plain();
        var dial = DropPlan.For(m, Cpu, new DropTarget(500, 300));
        var id = dial.Apply(m, dial.Options[0]);
        var plan = DropPlan.For(m, RamGb, new DropTarget(500, 300, id));
        Assert.All(plan.Options, o => Assert.IsType<TextDef>(o.Create));
        Assert.Equal("Text 64", plan.Options[0].Label);   // the label is the preset, shown on its own example
    }

    /// <summary>The brief's sequence, step 3: "CPU load" dropped on its own dial gives the "27%".</summary>
    [Fact]
    public void A_Value_On_A_Part_Already_Showing_It_Offers_Text()
    {
        var m = Plain();
        var dial = DropPlan.For(m, Cpu, new DropTarget(500, 300));
        var id = dial.Apply(m, dial.Options[0]);
        var plan = DropPlan.For(m, Cpu, new DropTarget(500, 300, id));
        Assert.Equal("Text 27%", plan.Options[0].Label);
        var text = Assert.IsType<TextDef>(m.Find(plan.Apply(m, plan.Options[0])));
        Assert.Equal("hardware.cpu | \"{0:0%}\"", Bind(text.Text));
        Assert.Equal(new Rect(440, 288, 120, 24), text.Rect);
    }

    [Fact]
    public void Another_Fraction_On_A_Dial_Rebinds_It()
    {
        var m = Plain();
        var dial = DropPlan.For(m, Cpu, new DropTarget(500, 300));
        var id = dial.Apply(m, dial.Options[0]);
        var plan = DropPlan.For(m, Ram, new DropTarget(500, 300, id));
        Assert.Equal("Fraction", Assert.Single(plan.Options).Property);
        plan.Apply(m, plan.Options[0]);
        Assert.Equal("hardware.ram", Bind(((DialDef)m.Find(id)!).Fraction));
    }

    [Fact]
    public void At_Layout_Depth_A_Drop_On_A_Copys_Part_Is_An_Override()
    {
        var m = DesignerModelDepthTests.Model();
        var before = m.ToJson();
        var plan = DropPlan.For(m, Ram, new DropTarget(140, 90, "dial-1.dial"));
        plan.Apply(m, plan.Options[0]);
        var copy = m.Layout.Copies!.Single();
        Assert.Equal("hardware.ram", Bind(copy.Overrides["components.dial.fraction"]));
        Assert.Equal("hardware", Assert.Single(m.Layout.Sources).Name);
        OneUndoEntry(m, before);
    }

    [Fact]
    public void At_Copy_Depth_A_Drop_On_A_Part_Is_An_Override_And_The_Source_Goes_To_The_Layout()
    {
        var m = DesignerModelDepthTests.Model();
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        var before = m.ToJson();
        var plan = DropPlan.For(m, Ram, new DropTarget(140, 90, "dial-1.dial"));
        Assert.Equal("dial-1.dial", plan.Apply(m, plan.Options[0]));
        var copy = m.Layout.Copies!.Single();
        Assert.Equal("hardware.ram", Bind(copy.Overrides["components.dial.fraction"]));
        Assert.Equal("hardware", Assert.Single(m.Layout.Sources).Name);
        Assert.Equal("hardware.ram", Bind(((DialDef)m.Find("dial-1.dial")!).Fraction));
        OneUndoEntry(m, before);

        // The dial already shows CPU load, and a copy cannot gain a text part: nothing to offer.
        Assert.Empty(DropPlan.For(m, Cpu, new DropTarget(140, 90, "dial-1.dial")).Options);
    }

    [Fact]
    public void At_Copy_Depth_Rebinding_To_The_Widgets_Value_Removes_The_Override()
    {
        var m = DesignerModelDepthTests.Model("""{ "id": "dial-1", "widget": "dial", "x": 100, "y": 50, "overrides": { "components.dial.fraction": { "bind": "hardware.ram" } } }""");
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        var plan = DropPlan.For(m, Cpu, new DropTarget(140, 90, "dial-1.dial"));
        plan.Apply(m, Assert.Single(plan.Options));
        Assert.Empty(m.Layout.Copies!.Single().Overrides);
    }

    [Fact]
    public void At_Copy_Depth_Empty_Canvas_Offers_Nothing()
    {
        var m = DesignerModelDepthTests.Model();
        m.SetDepth(Depth.Copy("dial-1", "dial"));
        Assert.Empty(DropPlan.For(m, Cpu, new DropTarget(700, 700)).Options);
    }

    [Fact]
    public void At_Widget_Depth_The_Part_And_Its_Source_Go_Into_The_Widget()
    {
        var m = DesignerModelDepthTests.Model();
        var copyId = Lens.NewWidget(m, 400, 200);
        var key = m.Depth.WidgetKey!;
        var plan = DropPlan.For(m, Cpu, new DropTarget(440, 240));
        var id = plan.Apply(m, plan.Options[0]);

        Assert.Equal(copyId + ".dial", id);
        Assert.IsType<DialDef>(m.Find(id));
        var widget = m.WidgetEdits[key];
        Assert.Equal("hardware", Assert.Single(widget.Sources).Name);
        Assert.Equal("hardware.cpu", Bind(((DialDef)Assert.Single(widget.Components)).Fraction));
        Assert.Empty(m.Layout.Sources);

        var again = DropPlan.For(m, Ram, new DropTarget(440, 240));
        again.Apply(m, again.Options[1]);
        Assert.Single(m.WidgetEdits[key].Sources);
        Assert.Equal(2, m.WidgetEdits[key].Components.Count);
    }
}
