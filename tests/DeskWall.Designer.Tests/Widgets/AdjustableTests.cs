using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>"Make adjustable": which properties may become a knob, what kind of knob, what it is
/// called and where its default comes from.</summary>
public class AdjustableTests
{
    private static PropertySchema.Prop Prop(ComponentDef def, string name)
        => PropertySchema.For(def).First(p => p.Name == name);

    // ---- editor -> knob type ---------------------------------------------------------------

    [Theory]
    [InlineData(PropertySchema.Editor.Text, KnobType.Text)]
    [InlineData(PropertySchema.Editor.Font, KnobType.Text)]
    [InlineData(PropertySchema.Editor.Path, KnobType.Text)]
    [InlineData(PropertySchema.Editor.Number, KnobType.Number)]
    [InlineData(PropertySchema.Editor.AutoNumber, KnobType.Number)]
    [InlineData(PropertySchema.Editor.Color, KnobType.Color)]
    [InlineData(PropertySchema.Editor.Enum, KnobType.Choice)]
    public void Each_Editor_Has_Its_Knob(PropertySchema.Editor editor, KnobType expected)
        => Assert.Equal(expected, Adjustable.TypeFor(editor));

    [Fact]
    public void A_Binding_Editor_Is_Not_Offered_At_All()
        => Assert.Null(Adjustable.TypeFor(PropertySchema.Editor.Binding));

    [Theory]
    [InlineData("every", KnobType.Number)]
    [InlineData("timeout", KnobType.Number)]
    [InlineData("url", KnobType.Text)]
    [InlineData("command", KnobType.Text)]
    public void A_Source_Setting_Is_Text_Unless_It_Is_A_Duration(string key, KnobType expected)
        => Assert.Equal(expected, Adjustable.TypeForSetting(key));

    // ---- what may be exposed -----------------------------------------------------------------

    [Fact]
    public void A_Bound_Property_Cannot_Be_Made_Adjustable()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        doc.Model.Edit("Bind", l => ((TextDef)l.Components[0]).Text = PropertyValue.Bound(Binding.Parse("time.now")));

        Assert.False(Adjustable.CanAdjust(doc.Model.Find(part.Id)!, Prop(part, "Text")));
        Assert.False(doc.ToggleAdjustable(part.Id, "Text"));
        Assert.Empty(doc.Adjustables);
    }

    [Fact]
    public void A_Repeaters_Items_Cannot_Be_Made_Adjustable()
    {
        var repeater = new RepeaterDef
        {
            Id = "r",
            Rect = new Core.Rect(0, 0, 10, 10),
            Items = PropertyValue.Bound(Binding.Parse("disks.drives")),
            Template = new List<ComponentDef>(),
        };
        Assert.False(Adjustable.CanAdjust(repeater, Prop(repeater, "Items")));
    }

    // ---- labels ------------------------------------------------------------------------------

    [Fact]
    public void The_Label_Is_The_Property_Name_Until_Two_Parts_Share_It()
    {
        var doc = WidgetDocument.New();
        var first = doc.AddPart(PartKind.Text);
        var second = doc.AddPart(PartKind.Text);

        Assert.True(doc.ToggleAdjustable(first.Id, "Text"));
        Assert.True(doc.ToggleAdjustable(second.Id, "Text"));

        Assert.Equal(["Text", "Text (text-2)"], doc.Adjustables.Select(a => a.Label));
        Assert.Equal(["text", "text-text-2"], doc.Adjustables.Select(a => a.Id));
    }

    [Fact]
    public void Toggling_Twice_Takes_The_Knob_Back_Off()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        Assert.True(doc.ToggleAdjustable(part.Id, "Text"));
        Assert.False(doc.ToggleAdjustable(part.Id, "Text"));
        Assert.Empty(doc.Adjustables);
        Assert.False(doc.IsAdjustable(part.Id, "Text"));
    }

    // ---- defaults -------------------------------------------------------------------------------

    [Fact]
    public void The_Default_Is_Read_At_Save_Time_Not_At_Expose_Time()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        doc.ToggleAdjustable(part.Id, "Text");
        Assert.Equal("Text", Assert.Single(doc.ToTemplate().Knobs).Default);

        doc.Model.Edit("Set Text", l => ((TextDef)l.Components[0]).Text = PropertyValue.Literal("Hello"));
        Assert.Equal("Hello", Assert.Single(doc.ToTemplate().Knobs).Default);
    }

    [Fact]
    public void A_Choice_Default_Comes_Back_In_The_Choice_Lists_Own_Spelling()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        doc.ToggleAdjustable(part.Id, "Align");

        var knob = Assert.Single(doc.ToTemplate().Knobs);
        Assert.Equal(KnobType.Choice, knob.Type);
        Assert.Equal("Left", knob.Default);
        Assert.Equal(PropertySchema.AlignChoices, knob.Choices);
    }

    [Fact]
    public void A_Number_Knob_Carries_The_Range_The_Author_Set()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        doc.ToggleAdjustable(part.Id, "Size");
        var target = Assert.Single(doc.Adjustables);
        target.Min = 8;
        target.Max = 96;

        var knob = Assert.Single(doc.ToTemplate().Knobs);
        Assert.Equal(KnobType.Number, knob.Type);
        Assert.Equal(8, knob.Min);
        Assert.Equal(96, knob.Max);
    }

    // ---- sets paths -------------------------------------------------------------------------------

    [Fact]
    public void A_Component_Knob_Writes_The_Property_In_Lower_Case()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        doc.ToggleAdjustable(part.Id, "EffectRadius");
        Assert.Equal(["components.text.effectradius"], Assert.Single(doc.ToTemplate().Knobs).Sets);
    }

    [Fact]
    public void A_Source_Setting_Knob_Writes_Its_Settings_Path()
    {
        var doc = WidgetDocument.New();
        doc.AddSource(new SourceDef { Name = "feed", Type = "http", Settings = { ["url"] = "https://example.com/x" } });
        Assert.True(doc.ToggleSettingAdjustable("feed", "url"));

        var knob = Assert.Single(doc.ToTemplate().Knobs);
        Assert.Equal(["sources.feed.settings.url"], knob.Sets);
        Assert.Equal("https://example.com/x", knob.Default);
        Assert.Equal(KnobType.Text, knob.Type);
    }

    /// <summary>"every" is <c>SourceDef.EverySeconds</c> and not a setting, so a knob that wrote
    /// <c>settings.every</c> would land somewhere every source factory ignores.</summary>
    [Fact]
    public void An_Every_Knob_Writes_The_Sources_Own_Field()
    {
        var doc = WidgetDocument.New();
        doc.AddSource(new SourceDef { Name = "feed", Type = "http", EverySeconds = 900, Settings = { ["url"] = "https://example.com/x" } });
        doc.ToggleSettingAdjustable("feed", "every");

        var knob = Assert.Single(doc.ToTemplate().Knobs);
        Assert.Equal(["sources.feed.every"], knob.Sets);
        Assert.Equal("900", knob.Default);
        Assert.Equal(KnobType.Number, knob.Type);
    }

    [Fact]
    public void Setting_An_Every_Knob_On_A_Placed_Instance_Changes_The_Refresh_Interval()
    {
        var doc = WidgetDocument.New();
        doc.AddSource(new SourceDef { Name = "feed", Type = "http", EverySeconds = 900, Settings = { ["url"] = "https://example.com/x" } });
        doc.AddPart(PartKind.Text);
        doc.ToggleSettingAdjustable("feed", "every");
        var template = doc.ToTemplate();

        var layout = new LayoutFile { BaseImage = "" };
        var instance = WidgetInstance.Add(layout, template, new Core.Rect(0, 0, 0, 0));
        WidgetInstance.SetKnob(layout, template, instance, template.Knobs[0].Id, "120");

        Assert.Equal(120, layout.Sources.Single(s => s.Name == "feed").EverySeconds);
    }

    // ---- the target going away ---------------------------------------------------------------------

    [Fact]
    public void Deleting_The_Part_Drops_Its_Knob()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        doc.ToggleAdjustable(part.Id, "Text");
        Assert.Single(doc.Adjustables);

        doc.Model.Remove([part.Id]);
        Assert.Empty(doc.Adjustables);
        Assert.Empty(doc.ToTemplate().Knobs);
    }

    [Fact]
    public void Removing_The_Source_Drops_Its_Knob()
    {
        var doc = WidgetDocument.New();
        doc.AddSource(new SourceDef { Name = "feed", Type = "http", Settings = { ["url"] = "https://example.com/x" } });
        doc.ToggleSettingAdjustable("feed", "url");
        Assert.Single(doc.Adjustables);

        doc.RemoveSource("feed");
        Assert.Empty(doc.Adjustables);
    }

    [Fact]
    public void Binding_An_Exposed_Property_Drops_Its_Knob()
    {
        var doc = WidgetDocument.New();
        var part = doc.AddPart(PartKind.Text);
        doc.ToggleAdjustable(part.Id, "Text");

        doc.Model.Edit("Bind", l => ((TextDef)l.Components[0]).Text = PropertyValue.Bound(Binding.Parse("time.now")));
        Assert.Empty(doc.Adjustables);
    }
}
