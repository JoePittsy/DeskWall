using System.IO;
using DeskWall.Core;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests.Widgets;

/// <summary>The one knob the editor can build out of a <em>bound</em> property: a Drive knob over
/// a binding that keys <c>disks.drives[C]</c>. One knob repoints every part of the widget at once,
/// and the letter only becomes "{drive}" in the saved file - the document on screen keeps the real
/// letter so the canvas goes on drawing real data.</summary>
public class DriveKnobTests
{
    private static WidgetDocument TwoPartDriveWidget()
    {
        var doc = WidgetDocument.New();
        doc.Name = "Drive C";
        doc.Description = "How full a drive is.";
        doc.AddSource(new SourceDef { Name = "disks", Type = "disks" });
        var bar = doc.AddPart(PartKind.Bar);
        var text = doc.AddPart(PartKind.Text);
        doc.Model.Edit("Bind", l =>
        {
            ((BarDef)l.Components[0]).Fraction = PropertyValue.Bound(Binding.Parse("disks.drives[C].usedFraction"));
            ((TextDef)l.Components[1]).Text = PropertyValue.Bound(Binding.Parse("disks.drives[C].freeGB | \"{0:N0} GB\""));
        });
        _ = bar;
        _ = text;
        return doc;
    }

    private static PropertySchema.Prop Prop(ComponentDef def, string name)
        => PropertySchema.For(def).First(p => p.Name == name);

    private static string BindingText(LayoutFile layout, string id, string property)
    {
        var def = layout.Components.First(c => c.Id == id);
        return Prop(def, property).Get(def)!.Binding!.ToString();
    }

    // ---- detection ---------------------------------------------------------------------------

    [Theory]
    [InlineData("disks.drives[C].usedFraction", "C")]
    [InlineData("disks.drives[c].freeGB | \"{0:N0} GB\"", "c")]
    [InlineData("disks.drives[{drive}].usedFraction", "{drive}")]
    public void A_Drive_Keyed_Binding_Is_Recognised(string text, string expected)
        => Assert.Equal(expected, Adjustable.DriveKey(Binding.Parse(text)));

    [Theory]
    [InlineData("disks.drives")]
    [InlineData("disks.drives[0].usedFraction")]
    [InlineData("disks.total")]
    [InlineData("hardware.drives[C].usedFraction")]
    [InlineData("time.now")]
    public void Anything_Else_Is_Not_A_Drive_Binding(string text)
        => Assert.Null(Adjustable.DriveKey(Binding.Parse(text)));

    [Fact]
    public void The_Knob_Toggle_Is_Offered_On_A_Drive_Keyed_Binding_And_Not_On_Another()
    {
        var doc = TwoPartDriveWidget();
        var bar = doc.Model.Layout.Components[0];
        Assert.True(Adjustable.CanAdjustAsDrive(bar, Prop(bar, "Fraction")));
        Assert.False(Adjustable.CanAdjust(bar, Prop(bar, "Fraction")));

        doc.Model.Edit("Rebind", l => ((BarDef)l.Components[0]).Fraction = PropertyValue.Bound(Binding.Parse("hardware.cpu")));
        var rebound = doc.Model.Layout.Components[0];
        Assert.False(Adjustable.CanAdjustAsDrive(rebound, Prop(rebound, "Fraction")));
    }

    // ---- one knob, two targets ------------------------------------------------------------------

    [Fact]
    public void A_Second_Drive_Target_Joins_The_Knob_Already_There()
    {
        var doc = TwoPartDriveWidget();
        Assert.True(doc.ToggleAdjustable("bar", "Fraction"));
        Assert.True(doc.ToggleAdjustable("text", "Text"));

        var target = Assert.Single(doc.Adjustables);
        Assert.True(target.IsDrive);
        Assert.Equal("Drive", target.Label);
        Assert.Equal([("bar", "Fraction"), ("text", "Text")], target.DriveTargets);

        var knob = Assert.Single(doc.ToTemplate().Knobs);
        Assert.Equal(KnobType.Drive, knob.Type);
        Assert.Equal("C", knob.Default);
        Assert.Equal(["components.bar.fraction:{drive}", "components.text.text:{drive}"], knob.Sets);
    }

    [Fact]
    public void Turning_One_Target_Off_Leaves_The_Knob_With_The_Other()
    {
        var doc = TwoPartDriveWidget();
        doc.ToggleAdjustable("bar", "Fraction");
        doc.ToggleAdjustable("text", "Text");

        Assert.False(doc.ToggleAdjustable("bar", "Fraction"));
        Assert.False(doc.IsAdjustable("bar", "Fraction"));
        Assert.True(doc.IsAdjustable("text", "Text"));
        Assert.Equal(["components.text.text:{drive}"], Assert.Single(doc.ToTemplate().Knobs).Sets);

        Assert.False(doc.ToggleAdjustable("text", "Text"));
        Assert.Empty(doc.Adjustables);
    }

    [Fact]
    public void Deleting_A_Part_Takes_Only_Its_Own_Target_Off_The_Knob()
    {
        var doc = TwoPartDriveWidget();
        doc.ToggleAdjustable("bar", "Fraction");
        doc.ToggleAdjustable("text", "Text");

        doc.Model.Remove(["bar"]);

        var target = Assert.Single(doc.Adjustables);
        Assert.Equal([("text", "Text")], target.DriveTargets);
        Assert.Equal(["components.text.text:{drive}"], Assert.Single(doc.ToTemplate().Knobs).Sets);
    }

    // ---- the letter lives on the canvas, the token lives in the file -------------------------------

    [Fact]
    public void The_Saved_Template_Carries_The_Token_And_The_Document_Keeps_The_Letter()
    {
        var doc = TwoPartDriveWidget();
        doc.ToggleAdjustable("bar", "Fraction");
        doc.ToggleAdjustable("text", "Text");

        var template = doc.ToTemplate();
        var saved = new LayoutFile { BaseImage = "", Components = template.Components.ToList() };
        Assert.Equal("disks.drives[{drive}].usedFraction", BindingText(saved, "bar", "Fraction"));
        Assert.Equal("disks.drives[{drive}].freeGB | \"{0:N0} GB\"", BindingText(saved, "text", "Text"));

        Assert.Equal("disks.drives[C].usedFraction", BindingText(doc.Model.Layout, "bar", "Fraction"));
        Assert.Equal("disks.drives[C].freeGB | \"{0:N0} GB\"", BindingText(doc.Model.Layout, "text", "Text"));
    }

    [Fact]
    public void Saving_Twice_Does_Not_Tokenise_Twice()
    {
        var doc = TwoPartDriveWidget();
        doc.ToggleAdjustable("bar", "Fraction");
        doc.ToggleAdjustable("text", "Text");

        var first = WidgetTemplateWriter.ToJson(doc.ToTemplate());
        var second = WidgetTemplateWriter.ToJson(doc.ToTemplate());
        Assert.Equal(first, second);
        Assert.Contains("disks.drives[{drive}].usedFraction", first, StringComparison.Ordinal);
    }

    // ---- round trip -----------------------------------------------------------------------------

    private static WidgetTemplate SaveAndLoad(WidgetTemplate t, string dirName)
    {
        var dir = Path.Combine(Paths.RuntimeDir, "drive-knob-tests", dirName);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, t.Key + ".json");
        File.WriteAllText(path, WidgetTemplateWriter.ToJson(t));
        return WidgetTemplate.Load(path);
    }

    [Fact]
    public void Re_Opening_A_Saved_Drive_Widget_Puts_The_Letter_Back_On_The_Canvas()
    {
        var doc = TwoPartDriveWidget();
        doc.ToggleAdjustable("bar", "Fraction");
        doc.ToggleAdjustable("text", "Text");
        var loaded = SaveAndLoad(doc.ToTemplate(), "reopen");

        var reopened = WidgetDocument.FromTemplate(loaded, null);

        // The canvas must draw real data, not a literal "{drive}" key that resolves to nothing.
        Assert.Equal("disks.drives[C].usedFraction", BindingText(reopened.Model.Layout, "bar", "Fraction"));
        Assert.Equal("disks.drives[C].freeGB | \"{0:N0} GB\"", BindingText(reopened.Model.Layout, "text", "Text"));

        // And the knob came back as an editable Drive knob, not as a pass-through.
        var target = Assert.Single(reopened.Adjustables);
        Assert.True(target.IsDrive);
        Assert.Equal([("bar", "Fraction"), ("text", "Text")], target.DriveTargets);
        Assert.Empty(reopened.PassThroughKnobs);

        // Re-saving is the same file again.
        Assert.Equal(WidgetTemplateWriter.ToJson(loaded), WidgetTemplateWriter.ToJson(reopened.ToTemplate()));
    }

    // ---- and it works once placed ------------------------------------------------------------------

    [Fact]
    public void A_Placed_Instance_Follows_The_Knob_On_Both_Parts()
    {
        var doc = TwoPartDriveWidget();
        doc.ToggleAdjustable("bar", "Fraction");
        doc.ToggleAdjustable("text", "Text");
        var template = SaveAndLoad(doc.ToTemplate(), "placed");

        var layout = new LayoutFile { BaseImage = "x.jpg" };
        var instance = WidgetInstance.Add(layout, template, new Rect(100, 200, 0, 0));

        Assert.Equal("disks.drives[C].usedFraction", BindingText(layout, $"{instance}.bar", "Fraction"));

        WidgetInstance.SetKnob(layout, template, instance, "drive", "D");

        Assert.Equal("disks.drives[D].usedFraction", BindingText(layout, $"{instance}.bar", "Fraction"));
        Assert.Equal("disks.drives[D].freeGB | \"{0:N0} GB\"", BindingText(layout, $"{instance}.text", "Text"));
        Assert.Equal("D", layout.Widgets![instance].Knobs["drive"]);
    }
}
