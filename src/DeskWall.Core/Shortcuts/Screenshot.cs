using DeskWall.Core.Render;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace DeskWall.Core.Shortcuts;

/// <summary>Capture the screen (physical pixels) into a Surface with GDI BitBlt + GetDIBits. No GPU,
/// no WinRT, no System.Drawing bitmap.</summary>
public static unsafe class Screenshot
{
    public static Surface Capture(Rect physical)
    {
        if (physical.W <= 0 || physical.H <= 0)
            throw new ArgumentOutOfRangeException(nameof(physical), "capture rect must be at least 1x1");
        int w = physical.W, h = physical.H;

        var screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull) throw new InvalidOperationException("GetDC(NULL) failed");
        try
        {
            var mem = PInvoke.CreateCompatibleDC(screen);
            if (mem.IsNull) throw new InvalidOperationException("CreateCompatibleDC failed");
            try
            {
                var bmp = PInvoke.CreateCompatibleBitmap(screen, w, h);
                if (bmp == default) throw new InvalidOperationException("CreateCompatibleBitmap failed");
                try
                {
                    var previous = PInvoke.SelectObject(mem, bmp);
                    // CAPTUREBLT so layered windows (and the desktop's own layered chrome) are included.
                    var rop = (ROP_CODE)((uint)ROP_CODE.SRCCOPY | (uint)ROP_CODE.CAPTUREBLT);
                    var blitted = PInvoke.BitBlt(mem, 0, 0, w, h, screen, physical.X, physical.Y, rop);
                    PInvoke.SelectObject(mem, previous);   // GetDIBits needs the bitmap out of the DC
                    if (!blitted) throw new InvalidOperationException("BitBlt from the screen failed");

                    var bytes = new byte[checked(w * h * 4)];
                    var info = default(BITMAPINFO);
                    info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
                    info.bmiHeader.biWidth = w;
                    info.bmiHeader.biHeight = -h;    // negative: top-down rows
                    info.bmiHeader.biPlanes = 1;
                    info.bmiHeader.biBitCount = 32;
                    info.bmiHeader.biCompression = 0;   // BI_RGB
                    fixed (byte* p = bytes)
                        if (PInvoke.GetDIBits(mem, bmp, 0, (uint)h, p, &info, DIB_USAGE.DIB_RGB_COLORS) == 0)
                            throw new InvalidOperationException("GetDIBits failed");

                    // GDI leaves the fourth byte at 0. The screen is opaque and Surface stores
                    // premultiplied BGRA, so alpha must read 255 or every pixel decodes as transparent.
                    for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
                    return Surface.FromBgra(w, h, bytes);
                }
                finally { PInvoke.DeleteObject(bmp); }
            }
            finally { PInvoke.DeleteDC(mem); }
        }
        finally { PInvoke.ReleaseDC(HWND.Null, screen); }
    }
}
