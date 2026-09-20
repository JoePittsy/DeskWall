using System.Windows;
using System.Windows.Threading;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;

namespace DeskWall.Designer;

/// <summary>Startup, and nothing else: find the display in front of the owner, find the layout the
/// daemon would draw on it, and hand both to the shell. With no layout at all the first-run picker
/// runs first; cancelling it exits, because a designer with nothing open has nothing to show.
/// <para>ShutdownMode is OnExplicitShutdown, not OnMainWindowClose: WPF makes the first window
/// instantiated the MainWindow, and that would be FirstRun, whose closing would then take the
/// application down before the shell ever opened. MainWindow.OnClosed does the shutdown.</para></summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var settings = Settings.Load();
        var store = LayoutStore.Default();
        var signature = PrimarySignature(settings);

        var resolution = store.Resolve(signature);
        if (resolution is null)
        {
            var first = new FirstRun(signature, store);
            if (first.ShowDialog() != true) { Shutdown(); return; }
            resolution = store.Resolve(signature);
            if (resolution is null)
            {
                MessageBox.Show($"No layout for {signature.Key}.", "DeskWall Designer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }
        }

        var window = new MainWindow(store, settings, signature, resolution);
        MainWindow = window;
        window.Show();
    }

    /// <summary>A fault in one panel must not cost the owner every unsaved edit in the document.
    /// The log keeps the type and the stack, the owner gets the one line that says what happened,
    /// and the designer stays up: there is nothing else useful to offer here, and a Continue/Quit
    /// choice would only ask the owner to guess.</summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { RollingLog.Default().Error("designer", e.Exception); }
        catch (Exception) { /* the log is a courtesy; never fail twice */ }
        MessageBox.Show(e.Exception.Message, "DeskWall Designer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>The primary monitor, or any monitor, or - only when the shell cannot enumerate at
    /// all, which happens over a session that is still coming up - the last display the designer was
    /// used on, so the owner can still edit that layout.</summary>
    private static DisplaySignature PrimarySignature(Settings settings)
    {
        try
        {
            var monitors = Monitors.Enumerate();
            var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor is not null) return monitor.Signature;
        }
        catch (Exception)
        {
            // fall through to the remembered signature
        }
        if (settings.LastSignatureKey is { } key)
        {
            try { return DisplaySignature.Parse(key); }
            catch (FormatException) { }
            catch (IndexOutOfRangeException) { }
        }
        return new DisplaySignature("unknown", 1920, 1080, 100);
    }
}
