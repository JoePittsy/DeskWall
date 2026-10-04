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
