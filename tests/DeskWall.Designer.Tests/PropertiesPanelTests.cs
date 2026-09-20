using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
using Xunit;

/// <summary>The panel is a WPF control, so each case runs on its own STA thread. Review finding 3:
/// a repeater whose "items" is a literal is a layout the app itself invites you to open, and
/// selecting it used to dereference a null Binding and take the designer down.</summary>
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

    [Fact]
    [Trait("Category", "Desktop")]
    public void Selecting_A_Repeater_With_Literal_Items_Shows_The_Literal_And_A_Note()
    {
        OnStaThread(() =>
        {
            var layout = LayoutFile.Parse(LiteralItemsLayout);
            Assert.False(((RepeaterDef)layout.Components[0]).Items.IsBound);   // the premise

            var model = new DesignerModel(layout, new DisplaySignature("T", 1000, 800, 100), null);
            var panel = new PropertiesPanel();
            panel.Attach(model);
            model.Select(["drives"]);

            var texts = panel.Root.Children.OfType<System.Windows.FrameworkElement>()
                .SelectMany(Descendants)
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .ToList();
            Assert.Contains("disks.drives", texts);
            Assert.Contains("items must be a binding", texts);
        });
    }

    private static System.Collections.Generic.IEnumerable<System.Windows.FrameworkElement> Descendants(System.Windows.FrameworkElement e)
    {
        yield return e;
        if (e is not Panel p) yield break;
        foreach (var child in p.Children.OfType<System.Windows.FrameworkElement>())
            foreach (var d in Descendants(child)) yield return d;
    }

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
