using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
using Xunit;

/// <summary>The properties panel is a WPF control, so each case runs on its own STA thread. The
/// panel is driven through its own controls, found by the AutomationId each row carries (the part
/// id, a slash, the property), and their context menus, exactly as a keyboard user reaches them.
/// Uses the shipped "dial" widget: parts dial, value and label; knobs Metric and Warn at.</summary>
public class PropertiesPanelTests
{
    private const string LiteralItemsLayout = """
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [
            { "type": "repeater", "id": "drives", "rect": [0, 0, 400, 300], "z": 1,
              "items": "disks.drives",
              "template": [
                { "type": "text", "id": "letter", "rect": [0, 0, 40, 20], "text": "C" } ] } ] }
        """;

    private static DesignerModel DialLayout(string overrides = "") => new(LayoutFile.Parse($$"""
        { "version": 2, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "text", "id": "byhand", "rect": [600, 600, 50, 20], "text": "x" } ],
          "copies": [
            { "id": "dial-1", "widget": "dial", "x": 100, "y": 50 {{overrides}} },
            { "id": "dial-2", "widget": "dial", "x": 300, "y": 50 } ] }
        """), new DisplaySignature("T", 1000, 800, 100), null);

    private static WidgetCopy Copy(DesignerModel m, string id) => m.Layout.Copies!.Single(c => c.Id == id);

    private static PropertiesPanel Panel(DesignerModel m)
    {
        var panel = new PropertiesPanel();
        panel.Attach(m);
        return panel;
    }

    /// <summary>Review finding 3: a repeater whose "items" is a literal used to dereference a null
    /// Binding and take the designer down.</summary>
    [Fact]
    public void Selecting_A_Repeater_With_Literal_Items_Shows_The_Literal_And_A_Note()
    {
        OnStaThread(() =>
        {
            var layout = LayoutFile.Parse(LiteralItemsLayout);
            Assert.False(((RepeaterDef)layout.Components[0]).Items.IsBound);   // the premise

            var model = new DesignerModel(layout, new DisplaySignature("T", 1000, 800, 100), null);
            var panel = Panel(model);
            model.Select(["drives"]);

            var texts = Descendants(panel.Root).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("disks.drives", texts);
            Assert.Contains("items must be a binding", texts);
        });
    }

    [Fact]
    public void Rows_Are_Grouped_With_Human_Labels_And_Every_Row_Control_Is_Named()
    {
        OnStaThread(() =>
        {
            var m = DialLayout();
            m.SetDepth(Depth.Copy("dial-1", "dial"));
            m.Select(["dial-1.dial"]);
            var panel = Panel(m);

            var texts = Descendants(panel.Root).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(["Knobs", "Content", "Colour", "Arc", "Geometry"],
                texts.Where(t => t is "Knobs" or "Content" or "Type" or "Colour" or "Arc" or "Geometry").ToList());
            Assert.Contains("Warn at", texts);
            Assert.Contains("Stroke width", texts);
            Assert.DoesNotContain("Threshold", texts);

            var unnamed = Descendants(panel.Root).OfType<Control>().Where(c => c.Focusable && c is not ComboBoxItem
                    && string.IsNullOrEmpty(AutomationProperties.GetName(c)))
                .Select(c => c.GetType().Name).ToList();
            Assert.Empty(unnamed);
        });
    }

    [Fact]
    public void Percentage_Rows_Show_Percent_And_Write_The_Fraction_Back()
    {
        OnStaThread(() =>
        {
            var m = DialLayout();
            m.SetDepth(Depth.Copy("dial-1", "dial"));
            m.Select(["dial-1.dial"]);
            var panel = Panel(m);

            var box = (TextBox)ById(panel, "dial-1.dial/Threshold");
            Assert.Equal("90", box.Text);   // 0.9 in the widget
            box.Text = "75 %";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal("0.75", Copy(m, "dial-1").Overrides["components.dial.threshold"].LiteralText);
            Assert.Equal("75", ((TextBox)ById(panel, "dial-1.dial/Threshold")).Text);   // rebuilt from the model

            box = (TextBox)ById(panel, "dial-1.dial/Threshold");
            box.Text = "90";   // the baseline again: the override goes (Overrides.Diff)
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Empty(Copy(m, "dial-1").Overrides);

            box = (TextBox)ById(panel, "dial-1.dial/Threshold");
            box.Text = "lots";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal("90", box.Text);   // put back, nothing written
            Assert.Empty(Copy(m, "dial-1").Overrides);
        });
    }

    [Fact]
    public void An_Overridden_Row_Is_Marked_And_Offers_Reset_And_Push_To_Widget()
    {
        OnStaThread(() =>
        {
            var m = DialLayout(""", "overrides": { "components.label.color": "#FFFF0000" }""");
            m.SetDepth(Depth.Copy("dial-1", "dial"));
            m.Select(["dial-1.label"]);
            var panel = Panel(m);

            var colour = (Control)ById(panel, "dial-1.label/Color");
            Assert.Equal("overridden", AutomationProperties.GetItemStatus(colour));
            Assert.StartsWith("Changed on this copy", AutomationProperties.GetHelpText(colour), StringComparison.Ordinal);
            Assert.Equal(["Bind to a live value...", "Reset to widget", "Push to widget"], Menu(colour));
            var size = (Control)ById(panel, "dial-1.label/Size");
            Assert.Equal("", AutomationProperties.GetItemStatus(size));
            Assert.Equal(["Bind to a live value..."], Menu(size));

            Click(colour, "Reset to widget");
            Assert.Empty(Copy(m, "dial-1").Overrides);
            Assert.Equal("Reset", m.LastEditLabel);
            colour = (Control)ById(panel, "dial-1.label/Color");
            Assert.Equal("", AutomationProperties.GetItemStatus(colour));

            m.Undo();
            colour = (Control)ById(panel, "dial-1.label/Color");
            Click(colour, "Push to widget");
            Assert.Empty(Copy(m, "dial-1").Overrides);
            var label = (TextDef)m.WidgetEdits["dial"].Components.Single(c => c.Id == "label");
            Assert.Equal("#FFFF0000", label.Color.LiteralText);
            // The other copy, which never overrode it, follows the widget.
            Assert.Equal("#FFFF0000", ((TextDef)m.Expanded().Layout.Components.Single(c => c.Id == "dial-2.label")).Color.LiteralText);
        });
    }

    /// <summary>Found in the live pass: Reset from the row menu rebuilds the panel, and the old Size
    /// box, losing the focus afterwards, used to write its stale "14" straight back.</summary>
    [Fact]
    public void A_Box_The_Rebuild_Replaced_Does_Not_Commit_Its_Stale_Text()
    {
        OnStaThread(() =>
        {
            var m = DialLayout(""", "overrides": { "components.label.size": "14" }""");
            m.SetDepth(Depth.Copy("dial-1", "dial"));
            m.Select(["dial-1.label"]);
            var panel = Panel(m);
            var old = (TextBox)ById(panel, "dial-1.label/Size");
            Click(old, "Reset to widget");
            old.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Empty(Copy(m, "dial-1").Overrides);
            Assert.Equal("Reset", m.LastEditLabel);
        });
    }

    [Fact]
    public void A_Colour_From_The_Picker_Is_One_Undo_Entry_And_An_Override_At_Copy_Depth()
    {
        OnStaThread(() =>
        {
            var m = DialLayout();
            m.SetDepth(Depth.Copy("dial-1", "dial"));
            m.Select(["dial-1.label"]);
            var panel = Panel(m);

            var swatch = (ToggleButton)ById(panel, "dial-1.label/Color");
            swatch.IsChecked = true;
            swatch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var picker = (ColorPicker)ById(panel, "dial-1.label/Color:picker");
            Assert.Equal("#A0FFFFFF", picker.Value);
            Assert.False(m.CanUndo);

            picker.HexBox.Text = "#FF00C000";
            picker.HexBox.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, 0, picker.HexBox, null) { RoutedEvent = UIElement.LostKeyboardFocusEvent });
            Assert.Equal("#FF00C000", Copy(m, "dial-1").Overrides["components.label.color"].LiteralText);
            Assert.Equal("#A0FFFFFF", ((TextDef)m.Expanded().Layout.Components.Single(c => c.Id == "dial-2.label")).Color.LiteralText);

            // The picker stays open across the rebuild, and one undo takes the whole change back.
            Assert.IsType<ColorPicker>(ById(panel, "dial-1.label/Color:picker"));
            m.Undo();
            Assert.False(m.CanUndo);
            Assert.Empty(Copy(m, "dial-1").Overrides);
        });
    }

    [Fact]
    public void The_Chip_Offers_Only_Values_That_Fit_And_Binds_By_Label_With_A_Preset()
    {
        OnStaThread(() =>
        {
            var tree = new RecordValue(new Dictionary<string, Value>
            {
                ["hardware"] = new RecordValue(new Dictionary<string, Value>
                {
                    ["cpu"] = new NumberValue(0.27),
                    ["cpuPct"] = new NumberValue(27),
                    ["gpuName"] = new TextValue("RTX"),
                }),
            });
            var sources = new[] { new SourceDef { Name = "hardware", Type = "hardware" } };
            var entries = ValueCatalog.From(tree, sources);

            // A number row: no text values on offer.
            Assert.DoesNotContain(BindingChip.Matching(entries, PropertySchema.Editor.Number, ""), e => e.Kind == ValueKind.Text);
            Assert.Equal("hardware.cpu", BindingChip.Matching(entries, PropertySchema.Editor.Number, "cpu load").First().Path);

            // A text row, through the panel: the label is loose, so the source is added to the layout.
            var m = new DesignerModel(LayoutFile.Parse("""
                { "version": 2, "baseImage": "x.jpg", "sources": [],
                  "components": [ { "type": "text", "id": "t", "rect": [0, 0, 50, 20], "text": "x" } ] }
                """), new DisplaySignature("T", 1000, 800, 100), null);
            m.Select(["t"]);
            var panel = Panel(m);
            Click((Control)ById(panel, "t/Text"), "Bind to a live value...");
            var chip = (BindingChip)ById(panel, "t/Text");
            Assert.True(chip.IsEditing);
            chip.Show(null, entries, tree);   // the running sources, which this test has none of
            chip.Search.Text = "cpu load";
            chip.Pick();

            var bound = ((TextDef)m.Layout.Components.Single()).Text;
            Assert.Equal("hardware.cpu | \"{0:0%}\"", bound.Binding!.ToString());
            Assert.Equal("hardware", Assert.Single(m.Layout.Sources).Name);
            Assert.Equal("Bind Text", m.LastEditLabel);
            Assert.Equal("CPU load · 27%", BindingChip.Describe(bound.Binding, entries, tree));
            chip = (BindingChip)ById(panel, "t/Text");
            Assert.False(chip.IsEditing);

            m.Undo();
            Assert.False(m.CanUndo);
            Assert.Empty(m.Layout.Sources);
        });
    }

    /// <summary>The layout's photo is a PropertyValue since the photo-per-phase lane, so its row
    /// binds like any other: Bind in the menu opens the chip, a pick binds the layout's baseImage
    /// (bringing the source along), and Unbind puts back a literal.</summary>
    [Fact]
    public void The_Layout_Photo_Row_Binds_And_Unbinds_Like_Any_Property()
    {
        OnStaThread(() =>
        {
            var tree = new RecordValue(new Dictionary<string, Value>
            {
                ["time"] = new RecordValue(new Dictionary<string, Value> { ["phase"] = new TextValue("dusk") }),
            });
            var entries = ValueCatalog.From(tree, [new SourceDef { Name = "time", Type = "time" }]);
            var m = new DesignerModel(LayoutFile.Parse("""
                { "version": 2, "baseImage": "x.jpg", "sources": [], "components": [] }
                """), new DisplaySignature("T", 1000, 800, 100), null);
            var panel = Panel(m);

            var change = (Control)ById(panel, "layout:photo");
            Assert.Equal(["Bind to a live value..."], Menu(change));
            Click(change, "Bind to a live value...");
            var chip = (BindingChip)ById(panel, "layout:photo-row");
            Assert.True(chip.IsEditing);
            chip.Show(null, entries, tree);
            chip.Search.Text = "phase of day";
            chip.Pick();

            Assert.True(m.Layout.BaseImage.IsBound);
            Assert.Equal("time.phase", m.Layout.BaseImage.Binding!.ToString());
            Assert.Equal("time", Assert.Single(m.Layout.Sources).Name);
            Assert.Equal("Bind photo", m.LastEditLabel);

            // Bound, the row is the chip, and its menu offers Unbind.
            chip = (BindingChip)ById(panel, "layout:photo-row");
            Assert.False(chip.IsEditing);
            var withMenu = Descendants(chip).OfType<Control>().First(c => c.ContextMenu is not null);
            Assert.Contains("Unbind", Menu(withMenu));
            Click(withMenu, "Unbind");
            Assert.False(m.Layout.BaseImage.IsBound);
            Assert.Equal("Unbind photo", m.LastEditLabel);

            m.Undo();
            Assert.True(m.Layout.BaseImage.IsBound);
            m.Undo();
            Assert.Equal("x.jpg", m.Layout.BaseImage.LiteralText);
            Assert.Empty(m.Layout.Sources);
        });
    }

    [Fact]
    public void Expose_As_Knob_At_Widget_Depth_Adds_A_Knob_To_The_Widget_And_Undoes()
    {
        OnStaThread(() =>
        {
            var m = DialLayout();
            m.SetDepth(Depth.Widget("dial", "dial-1"));
            m.Select(["dial-1.label"]);
            var panel = Panel(m);

            var size = (Control)ById(panel, "dial-1.label/Size");
            Assert.Equal(["Bind to a live value...", "Expose as knob"], Menu(size));
            Click(size, "Expose as knob");

            var knobs = m.WidgetEdits["dial"].Knobs;
            var knob = Assert.Single(knobs, k => k.Sets.Contains("components.label.size"));
            Assert.Equal("11", knob.Default);
            Assert.Equal(["metric", "warnAt"], knobs.Where(k => k != knob).Select(k => k.Id));   // the others pass through
            Assert.Equal(m.Finder()("dial")!.Components.Count, m.WidgetEdits["dial"].Components.Count);
            var item = MenuItem((Control)ById(panel, "dial-1.label/Size"), "Expose as knob");
            Assert.True(item.IsChecked);

            m.Undo();
            Assert.False(m.CanUndo);
            Assert.Empty(m.WidgetEdits);
        });
    }

    [Fact]
    public void A_Selected_Copy_Shows_Its_Knobs_First_And_Edit_Parts_Is_The_Details_Path()
    {
        OnStaThread(() =>
        {
            var m = DialLayout();
            m.Select(["dial-1"]);
            var panel = Panel(m);
            string? removed = null;
            panel.RemoveRequested += id => removed = id;

            var metric = (ComboBox)ById(panel, "knob/metric");
            Assert.Equal("Metric", AutomationProperties.GetName(metric));
            metric.SelectedItem = metric.Items.OfType<ComboBoxItem>().Single(i => (string)i.Content == "GPU");
            Assert.StartsWith("GPU||", Copy(m, "dial-1").Knobs["metric"], StringComparison.Ordinal);
            Assert.Equal("Set Metric", m.LastEditLabel);

            ((Button)ById(panel, "copy:remove")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("dial-1", removed);

            ((Button)ById(panel, "copy:edit")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Depth.Copy("dial-1", "dial"), m.Depth);
            Assert.Equal(["dial-1.dial"], m.Selection);
            // Copy depth: the knobs still come first, then the part's own rows.
            Assert.IsType<ComboBox>(ById(panel, "knob/metric"));
            var box = (TextBox)ById(panel, "dial-1.dial/Thickness");
            box.Text = "9";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal("9", Copy(m, "dial-1").Overrides["components.dial.thickness"].LiteralText);
            Assert.Empty(m.WidgetEdits);
        });
    }

    [Fact]
    public void A_Template_Child_Shown_From_Layers_Edits_That_Child()
    {
        OnStaThread(() =>
        {
            var layout = LayoutFile.Parse(LiteralItemsLayout);
            var model = new DesignerModel(layout, new DisplaySignature("T", 1000, 800, 100), null);
            var panel = Panel(model);
            var repeater = (RepeaterDef)model.Layout.Components[0];
            panel.ShowTemplateChild(repeater.Template[0], repeater);

            var box = (TextBox)ById(panel, "letter/Text");
            box.Text = "D";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal("D", ((TextDef)((RepeaterDef)model.Layout.Components[0]).Template[0]).Text.LiteralText);
        });
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static IEnumerable<DependencyObject> Descendants(DependencyObject e)
    {
        yield return e;
        foreach (var child in LogicalTreeHelper.GetChildren(e).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }

    private static DependencyObject ById(PropertiesPanel panel, string id)
        => Descendants(panel.Root).First(d => d is UIElement u && AutomationProperties.GetAutomationId(u) == id);

    private static List<string> Menu(Control c) => c.ContextMenu?.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList() ?? [];

    private static MenuItem MenuItem(Control c, string header) => c.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == header);

    private static void Click(Control c, string header) => MenuItem(c, header).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));

    /// <summary>Runs the body on an STA thread and rethrows whatever it threw, so a regression shows
    /// up as the original exception rather than a thread that quietly died. A machine with no
    /// desktop session cannot create WPF controls at all; that case reports passed-trivially, the
    /// same convention the desktop-bound Core tests use.</summary>
    private static void OnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var noDesktop = false;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TypeInitializationException) { noDesktop = true; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Dispatcher")) { noDesktop = true; }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (noDesktop) return;
        failure?.Throw();
    }
}
