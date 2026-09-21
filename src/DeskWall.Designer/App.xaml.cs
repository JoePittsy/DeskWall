using System.Windows;
using System.Windows.Threading;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;

namespace DeskWall.Designer;

/// <summary>Startup, and nothing else: find the display in front of the owner, find the layout the
/// daemon would draw on it, and hand both to the window. There is no first-run picker any more -
/// a display with no layout gets an empty one and the gallery, which is a better first screen than
/// a list of starters nobody can picture.
/// <para>ShutdownMode is OnExplicitShutdown rather than OnMainWindowClose so a dialog opened before
/// the window cannot take the application down with it; MainWindow.OnClosed does the shutdown.</para></summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var settings = Settings.Load();
        var store = LayoutStore.Default();
        var signature = PrimarySignature(settings);

        var window = new MainWindow(store, settings, signature, store.Resolve(signature));
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
