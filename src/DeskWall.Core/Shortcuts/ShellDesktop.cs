using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using ShellCoClass = Windows.Win32.UI.Shell.Shell;

namespace DeskWall.Core.Shortcuts;

/// <summary>Shell.Application MinimizeAll / UndoMinimizeALL, the pair poc/verify.ps1 uses to expose the
/// desktop for a screenshot. Lifted out of <see cref="Calibrator"/> so `verify` and `calibrate` share
/// one copy. Never throws for a shell failure; it reports it, and both callers treat a failed
/// minimise as fatal rather than screenshot whatever is covering the desktop.</summary>
public static unsafe class ShellDesktop
{
    /// <summary>True when the shell really was asked to minimise. False means the desktop is not
    /// exposed; `verify` and `calibrate` stop there (exit 1).</summary>
    public static bool MinimizeAll() => Dispatch(minimize: true);

    public static bool UndoMinimizeAll() => Dispatch(minimize: false);

    private static bool Dispatch(bool minimize)
    {
        Com.EnsureInitialized();
        IShellDispatch* shell;
        var clsid = typeof(ShellCoClass).GUID;
        var iid = typeof(IShellDispatch).GUID;
        // Shell Automation Service is registered under InprocServer32 only - there is no
        // LocalServer32 key - so CLSCTX_LOCAL_SERVER on its own returns REGDB_E_CLASSNOTREG and this
        // method quietly did nothing, leaving every screenshot to capture the foreground window.
        const CLSCTX Context = CLSCTX.CLSCTX_INPROC_SERVER | CLSCTX.CLSCTX_LOCAL_SERVER;
        if (PInvoke.CoCreateInstance(&clsid, null, Context, &iid, (void**)&shell).Failed) return false;
        try
        {
            if (minimize) shell->MinimizeAll();
            else shell->UndoMinimizeALL();
            return true;
        }
        catch (COMException) { return false; }
        finally { shell->Release(); }
    }
}
