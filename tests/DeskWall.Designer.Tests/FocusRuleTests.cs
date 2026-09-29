using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskWall.Designer.Views;
using Xunit;

/// <summary>Critique 3, P1-b: Apply clears the focus so a knob being typed into commits, and used
/// to leave it cleared, so no key reached anything and Esc could not climb. The rule it now follows:
/// focus goes back to what had it, or to the canvas when that is gone.</summary>
public class FocusRuleTests
{
    [Fact]
    [Trait("Category", "Desktop")]
    public void Focus_Goes_Back_To_What_Had_It_Or_To_The_Fallback_When_That_Is_Gone()
    {
        OnStaThread(() =>
        {
            // A button, not a text box: the test host runs in invariant-globalization mode, where a
            // focused TextBox throws building its caret.
            var knob = new Button { Content = "knob" };
            var canvas = new Button { Content = "canvas" };
            var panel = new StackPanel();
            panel.Children.Add(knob);
            panel.Children.Add(canvas);
            var window = new Window { Content = panel, Width = 200, Height = 120, ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000 };
            window.Show();
            try
            {
                window.Activate();
                Assert.True(knob.Focus());

                var was = Keyboard.FocusedElement;
                Keyboard.ClearFocus();
                Assert.Null(Keyboard.FocusedElement);
                MainWindow.RestoreFocus(was, canvas);
                Assert.Same(knob, Keyboard.FocusedElement);

                // The knob's row is rebuilt by the commit: its box is gone, so the canvas takes it.
                Keyboard.ClearFocus();
                panel.Children.Remove(knob);
                MainWindow.RestoreFocus(was, canvas);
                Assert.Same(canvas, Keyboard.FocusedElement);

                // Nothing had focus before: the canvas, never nothing.
                Keyboard.ClearFocus();
                MainWindow.RestoreFocus(null, canvas);
                Assert.Same(canvas, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    private static void OnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
