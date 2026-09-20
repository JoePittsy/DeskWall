using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using IServiceProvider = Windows.Win32.System.Com.IServiceProvider;

namespace DeskWall.Core.Shortcuts;

/// <summary>The subset of FOLDERFLAGS this tool touches. CsWin32 generates its own FOLDERFLAGS as an
/// internal type (all generated types are internal under this project's settings), so it cannot appear
/// in a public signature; these values are the same bits.</summary>
[Flags]
public enum DesktopFolderFlags : uint
{
    None = 0,
    /// <summary>FWF_AUTOARRANGE</summary>
    AutoArrange = 0x0001,
    /// <summary>FWF_SNAPTOGRID</summary>
    SnapToGrid = 0x0004,
    /// <summary>FWF_NOICONS</summary>
    NoIcons = 0x1000,
    /// <summary>FWF_HIDEFILENAMES. The calibrator sets it for the length of one measurement so no icon
    /// label lands in the pixel diff, then puts it back.</summary>
    HideFileNames = 0x20000,
}

/// <summary>
/// The desktop's IFolderView2, reached the way poc/DeskIcons.cs does:
/// ShellWindows.FindWindowSW(CSIDL_DESKTOP) -> IServiceProvider.QueryService(SID_STopLevelBrowser,
/// IShellBrowser) -> QueryActiveShellView -> IFolderView2. Every call re-acquires the view (Explorer
/// restarts invalidate it) and releases everything before returning.
/// </summary>
public static unsafe class DesktopView
{
    /// <summary>Caller must Release the returned pointer.</summary>
    private static IFolderView2* Acquire()
    {
        Com.EnsureInitialized();
        IShellWindows* windows;
        var clsid = typeof(ShellWindows).GUID;
        var iid = typeof(IShellWindows).GUID;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, &iid, (void**)&windows).ThrowOnFailure();
        try
        {
            var loc = new VARIANT();
            loc.Anonymous.Anonymous.vt = VARENUM.VT_I4;
            loc.Anonymous.Anonymous.Anonymous.lVal = 0;   // CSIDL_DESKTOP
            var root = new VARIANT();                     // VT_EMPTY
            int hwnd;
            var disp = windows->FindWindowSW(&loc, &root, ShellWindowTypeConstants.SWC_DESKTOP, &hwnd,
                ShellWindowFindWindowOptions.SWFO_NEEDDISPATCH);
            if (disp is null) throw new InvalidOperationException("desktop shell window not found (is Explorer running?)");
            try
            {
                IServiceProvider* sp;
                var iidSp = typeof(IServiceProvider).GUID;
                ((IUnknown*)disp)->QueryInterface(&iidSp, (void**)&sp).ThrowOnFailure();
                try
                {
                    IShellBrowser* browser;
                    var sid = PInvoke.SID_STopLevelBrowser;
                    var iidSb = typeof(IShellBrowser).GUID;
                    sp->QueryService(&sid, &iidSb, (void**)&browser);
                    try
                    {
                        IShellView* view;
                        browser->QueryActiveShellView(&view);
                        try
                        {
                            IFolderView2* fv;
                            var iidFv = typeof(IFolderView2).GUID;
                            ((IUnknown*)view)->QueryInterface(&iidFv, (void**)&fv).ThrowOnFailure();
                            return fv;
                        }
                        finally { view->Release(); }
                    }
                    finally { browser->Release(); }
                }
                finally { sp->Release(); }
            }
            finally { disp->Release(); }
        }
        finally { windows->Release(); }
    }

    /// <summary>The single-level (child) pidl of a desktop file. Caller frees it with ILFree.</summary>
    private static ITEMIDLIST* ChildPidl(string path)
    {
        PInvoke.SHParseDisplayName(path, null, out ITEMIDLIST* abs, 0).ThrowOnFailure();
        try
        {
            // ILFindLastID returns a pointer INTO abs; clone it so abs can be freed here.
            return PInvoke.ILClone(PInvoke.ILFindLastID(abs));
        }
        finally { PInvoke.ILFree(abs); }
    }

    public static bool IsAvailable()
    {
        try
        {
            var fv = Acquire();
            try { return !Flags(fv).HasFlag(DesktopFolderFlags.NoIcons); }
            finally { fv->Release(); }
        }
        catch (Exception) { return false; }
    }

    /// <summary>null when the item is not on the desktop (or the view does not know it yet).</summary>
    public static (int X, int Y)? GetPosition(string desktopFilePath)
    {
        if (!File.Exists(desktopFilePath)) return null;
        var fv = Acquire();
        try
        {
            var pidl = ChildPidl(desktopFilePath);
            try
            {
                Point pt;
                fv->GetItemPosition(pidl, &pt);
                return (pt.X, pt.Y);
            }
            catch (COMException) { return null; }
            finally { PInvoke.ILFree(pidl); }
        }
        finally { fv->Release(); }
    }

    /// <summary>SelectAndPositionItems with SVSI_POSITIONITEM. Items missing from the desktop throw
    /// FileNotFoundException naming the path.</summary>
    public static void Position(IReadOnlyList<(string Path, int X, int Y)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var it in items)
            if (!File.Exists(it.Path))
                throw new FileNotFoundException("shortcut is not on the desktop", it.Path);
        if (items.Count == 0) return;

        var fv = Acquire();
        var pidls = new ITEMIDLIST*[items.Count];
        try
        {
            var pts = new Point[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                pidls[i] = ChildPidl(items[i].Path);
                pts[i] = new Point(items[i].X, items[i].Y);
            }
            fixed (ITEMIDLIST** pp = pidls)
            fixed (Point* ppt = pts)
                fv->SelectAndPositionItems((uint)items.Count, pp, ppt, (uint)_SVSIF.SVSI_POSITIONITEM);
        }
        finally
        {
            foreach (var p in pidls) if (p is not null) PInvoke.ILFree(p);
            fv->Release();
        }
    }

    /// <summary>The icon grid cell: 76x98 at 48 px icons, 100 percent scale.</summary>
    public static (int X, int Y) Spacing()
    {
        var fv = Acquire();
        try
        {
            var pt = new Point();
            fv->GetSpacing(&pt);
            return (pt.X, pt.Y);
        }
        finally { fv->Release(); }
    }

    /// <summary>Icon edge in pixels: 48 for the default medium icons.</summary>
    public static int IconSize()
    {
        var fv = Acquire();
        try
        {
            FOLDERVIEWMODE mode;
            int size;
            fv->GetViewModeAndIconSize(&mode, &size);
            return size;
        }
        finally { fv->Release(); }
    }

    public static DesktopFolderFlags Flags()
    {
        var fv = Acquire();
        try { return Flags(fv); }
        finally { fv->Release(); }
    }

    private static DesktopFolderFlags Flags(IFolderView2* fv)
    {
        uint f;
        fv->GetCurrentFolderFlags(&f);
        return (DesktopFolderFlags)f;
    }

    public static void SetFlags(DesktopFolderFlags mask, DesktopFolderFlags value)
    {
        var fv = Acquire();
        try { fv->SetCurrentFolderFlags((uint)mask, (uint)value); }
        finally { fv->Release(); }
    }
}
