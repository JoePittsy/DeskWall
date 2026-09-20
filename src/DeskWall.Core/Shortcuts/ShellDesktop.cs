using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using ShellCoClass = Windows.Win32.UI.Shell.Shell;

namespace DeskWall.Core.Shortcuts;

/// <summary>Shell.Application MinimizeAll / UndoMinimizeALL, the pair poc/verify.ps1 uses to expose the
/// desktop for a screenshot. Lifted out of <see cref="Calibrator"/> so `verify` and `calibrate` share
/// one copy. Best effort throughout: failing to minimise is not worth losing the run that wanted it.</summary>
public static unsafe class ShellDesktop
{
    public static void MinimizeAll() => Dispatch(minimize: true);

    public static void UndoMinimizeAll() => Dispatch(minimize: false);

    private static void Dispatch(bool minimize)
    {
        Com.EnsureInitialized();
        IShellDispatch* shell;
        var clsid = typeof(ShellCoClass).GUID;
        var iid = typeof(IShellDispatch).GUID;
        if (PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, &iid, (void**)&shell).Failed) return;
        try
        {
            if (minimize) shell->MinimizeAll();
            else shell->UndoMinimizeALL();
        }
        catch (COMException) { }
        finally { shell->Release(); }
    }
}
