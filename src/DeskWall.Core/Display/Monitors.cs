using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;

namespace DeskWall.Core.Display;

public static unsafe class Monitors
{
    private sealed record Raw(Rect Bounds, bool Primary, int Dpi, string GdiName);

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var raws = new List<Raw>();
        var handle = GCHandle.Alloc(raws);
        try
        {
            PInvoke.EnumDisplayMonitors(HDC.Null, null, &Callback, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally { handle.Free(); }

        // Join with IDesktopWallpaper for the per-monitor id SetWallpaper needs.
        Com.EnsureInitialized();
        var clsid = typeof(DesktopWallpaper).GUID;
        var iid = typeof(IDesktopWallpaper).GUID;
        IDesktopWallpaper* dw;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, &iid, (void**)&dw).ThrowOnFailure();
        try
        {
            uint count;
            dw->GetMonitorDevicePathCount(&count);
            var result = new List<MonitorInfo>();
            for (uint i = 0; i < count; i++)
            {
                PWSTR id;
                dw->GetMonitorDevicePathAt(i, &id);
                string idStr = id.ToString();
                RECT rc;
                try
                {
                    dw->GetMonitorRECT(id, &rc);   // must run before the id is freed
                }
                catch (COMException)
                {
                    continue; // detached monitor
                }
                finally { PInvoke.CoTaskMemFree(id); }

                var bounds = new Rect(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
                var raw = raws.FirstOrDefault(r => r.Bounds == bounds);
                if (raw is null) continue;
                var scale = (int)Math.Round(raw.Dpi / 96.0 * 100);
                result.Add(new MonitorInfo(new DisplaySignature(idStr, bounds.W, bounds.H, scale), bounds, raw.Primary, idStr));
            }
            return result;
        }
        finally { dw->Release(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Callback(HMONITOR mon, HDC hdc, RECT* rc, LPARAM lp)
    {
        var raws = (List<Raw>)GCHandle.FromIntPtr(lp).Target!;
        var mi = new MONITORINFOEXW();
        mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        PInvoke.GetMonitorInfo(mon, (MONITORINFO*)&mi);
        uint dx, dy;
        PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dx, &dy);
        var r = mi.monitorInfo.rcMonitor;
        raws.Add(new Raw(new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top), (mi.monitorInfo.dwFlags & 1) != 0, (int)dx, mi.szDevice.ToString()));
        return true;
    }
}
