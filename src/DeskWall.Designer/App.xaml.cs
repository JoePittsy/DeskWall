using System.Windows;
using System.Windows.Threading;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
// Qualified via usings rather than inline: inside a class deriving from Application, the name
// "Windows" binds to the inherited Application.Windows property before it reaches the namespace,
// so "Windows.Win32.PInvoke.X" does not compile here.
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DeskWall.Designer;

/// <summary>Startup, and nothing else: find the display in front of the owner, find the layout the
/// daemon would draw on it, and hand both to the window. There is no first-run picker any more -
/// a display with no layout gets an empty one and the gallery, which is a better first screen than
/// a list of starters nobody can picture.
/// <para>ShutdownMode is OnExplicitShutdown rather than OnMainWindowClose so a dialog opened before
/// the window cannot take the application down with it; MainWindow.OnClosed does the shutdown.</para></summary>
public partial class App : Application
{
    /// <summary>Held for the life of the process; releasing it is what lets the next designer start.</summary>
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!ClaimSingleInstance()) return;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var settings = Settings.Load();
        var store = LayoutStore.Default();
        var signature = PrimarySignature(settings);

        var window = new MainWindow(store, settings, signature, store.Resolve(signature));
        MainWindow = window;
        window.Show();
    }

    /// <summary>One designer per session, because two of them edit the same files. The designer is not
    /// a viewer: it writes the layout, <c>settings.json</c> and <c>secrets.json</c> in the runtime
    /// directory, and a second copy started while the first has unsaved edits will happily save over
    /// them - last writer wins, silently, on the files that paint the desktop.
    /// <para>A second instance hands focus to the first and leaves rather than opening a window, so
    /// clicking the tray item twice (or a tray that reports one click as two) reads as "the designer
    /// is already there" instead of producing a rival copy.</para></summary>
    private bool ClaimSingleInstance()
    {
        // Local\ rather than Global\: the runtime directory is per-user, so the thing being guarded
        // is one interactive session, not the machine.
        _instance = new Mutex(initiallyOwned: true, @"Local\DeskWall.Designer", out var mine);
        if (mine) return true;
        _instance.Dispose();
        _instance = null;
        ActivateExisting();
        Shutdown(0);
        return false;
    }

    /// <summary>Bring the designer that is already running to the front. Best effort: if it cannot be
    /// found (still starting, or its window not created yet) this instance still exits, because two
    /// designers is the thing being prevented - a missed activation is a worse click, not a hazard.</summary>
    private static void ActivateExisting()
    {
        var me = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("DeskWall.Designer"))
        {
            using (p)
            {
                if (p.Id == me || p.MainWindowHandle == IntPtr.Zero) continue;
                var h = (HWND)p.MainWindowHandle;
                if (PInvoke.IsIconic(h)) PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
                PInvoke.SetForegroundWindow(h);
                return;
            }
        }
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
