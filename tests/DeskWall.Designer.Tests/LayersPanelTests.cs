using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
using Xunit;

/// <summary>The Layers panel, driven through its own controls on an STA thread (as in
/// PropertiesPanelTests). Uses the shipped "dial" widget.</summary>
public class LayersPanelTests
{
    [Fact]
    public void An_Orphan_Row_Offers_Remove_And_Only_An_Orphan_Row_Does()
    {
        OnStaThread(() =>
        {
            var m = DesignerModelDepthTests.Model("""
                { "id": "dial-1", "widget": "dial", "x": 100, "y": 50,
                  "knobs": { "gone": "1" }, "overrides": { "components.gone.color": "#FF000000", "components.label.size": 20 } }
                """);
            var panel = new LayersPanel();
            panel.Attach(m);
            var buttons = Descendants(panel).OfType<Button>().Select(AutomationProperties.GetName).Order().ToList();
            Assert.Equal(["Remove orphan knob gone", "Remove orphan override components.gone.color"], buttons);

            Button("Remove orphan override components.gone.color").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(["components.label.size"], m.Layout.Copies![0].Overrides.Keys);
            Assert.Equal(["Remove orphan knob gone"], Descendants(panel).OfType<Button>().Select(AutomationProperties.GetName));

            Button("Remove orphan knob gone").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Empty(m.Layout.Copies![0].Knobs);
            Assert.DoesNotContain(LayerTree.Flatten(panel.Rows), r => r.IsOrphan);

            m.Undo();
            Assert.Single(Descendants(panel).OfType<Button>());

            Button Button(string name) => Descendants(panel).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        });
    }

    private static DesignerModel OneOrphan() => DesignerModelDepthTests.Model("""
        { "id": "dial-1", "widget": "dial", "x": 100, "y": 50, "overrides": { "components.gone.color": "#FF000000" } }
        """);

    private static TreeViewItem Item(LayersPanel panel, Func<LayerRow, bool> pick) => Items(panel.Tree).First(i => i.Tag is LayerRow r && pick(r));

    private static IEnumerable<TreeViewItem> Items(ItemsControl c)
    {
        foreach (var i in c.Items.OfType<TreeViewItem>())
        {
            yield return i;
            foreach (var x in Items(i)) yield return x;
        }
    }

    /// <summary>The window's Delete asks Layers first. With the focus elsewhere (the canvas), an
    /// orphan row the tree still has selected must not take the key from the canvas.</summary>
    [Fact]
    public void Delete_Is_Not_Layers_When_Layers_Has_No_Focus()
    {
        OnStaThread(() =>
        {
            var m = OneOrphan();
            var panel = new LayersPanel();
            panel.Attach(m);
            Item(panel, r => r.Kind == LayerKind.Orphan).IsSelected = true;
            Assert.False(panel.RemoveFocusedOrphan());
            Assert.Single(m.Layout.Copies![0].Overrides);
        });
    }

    /// <summary>The bug #80 found on the way: Delete on a focused orphan row fell through to the
    /// canvas selection, which an orphan row sets to its copy, and deleted the whole copy.</summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public void Delete_On_A_Focused_Orphan_Row_Removes_The_Orphan_And_Keeps_The_Copy()
    {
        OnStaThread(() =>
        {
            var m = OneOrphan();
            var panel = new LayersPanel();
            panel.Attach(m);
            var window = new Window { Content = panel, Width = 360, Height = 300, ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000 };
            window.Show();
            try
            {
                window.Activate();
                Item(panel, r => r.Kind == LayerKind.Copy).IsExpanded = true;
                window.UpdateLayout();   // the children's containers exist only after a layout pass
                var part = Item(panel, r => r.Kind == LayerKind.Part);
                part.IsSelected = true;
                Assert.True(part.Focus());
                Assert.False(panel.RemoveFocusedOrphan());   // a part row: Delete is the canvas's

                var orphan = Item(panel, r => r.Kind == LayerKind.Orphan);
                orphan.IsSelected = true;
                Assert.True(orphan.Focus());
                Assert.True(panel.RemoveFocusedOrphan());
                Assert.Single(m.Layout.Copies!);
                Assert.Empty(m.Layout.Copies![0].Overrides);
                Assert.True(panel.IsKeyboardFocusWithin);   // on the copy's row, not lost
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject e)
    {
        yield return e;
        foreach (var child in LogicalTreeHelper.GetChildren(e).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }

    /// <summary>Runs the body on an STA thread and rethrows whatever it threw; a machine with no
    /// desktop session cannot create WPF controls and reports passed-trivially.</summary>
    private static void OnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var noDesktop = false;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TypeInitializationException) { noDesktop = true; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Dispatcher", StringComparison.Ordinal)) { noDesktop = true; }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (noDesktop) return;
        failure?.Throw();
    }
}
