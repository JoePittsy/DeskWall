using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.Graphics.Imaging.D2D;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

// Test-only visibility for Surface.LiveCount, which lets the two exception-path-leak regression
// tests for finding 7 assert no net increase in live surfaces across a call that throws.
[assembly: InternalsVisibleTo("DeskWall.Core.Tests")]

namespace DeskWall.Core.Render;

/// <summary>
/// A premultiplied-BGRA bitmap (WIC) drawn through a software Direct2D render target. No
/// hardware D3D device is created: Direct2D's SOFTWARE type runs on WARP in-process.
/// Call shapes verified in docs/superpowers/plans/2026-09-20-phase1-spike-results.md.
/// </summary>
public sealed unsafe class Surface : IDisposable
{
    private const uint LockRead = 1, LockWrite = 2;
    private static readonly byte[] RawMagic = "DWRAW1\0\0"u8.ToArray();

    // Process-wide factories, created once on first use. No GPU; two small COM objects.
    private static IWICImagingFactory2* s_wic;
    private static ID2D1Factory* s_d2d;
    private static IDWriteFactory* s_dw;
    private static readonly object s_lock = new();
    // Finding 8: the three pointers stay non-volatile; this flag is the one write ordered after
    // them and the one read ordered before using them, which the .NET memory model guarantees for
    // a volatile field but does not guarantee for a plain-pointer double-checked read on every
    // architecture (only x64's store ordering happened to make the old check safe).
    private static volatile bool s_ready;

    private IWICBitmap* _bmp;
    private ID2D1RenderTarget* _rt;

    /// <summary>Surfaces created but not yet disposed. Test-only (finding 7 regression tests);
    /// production code never reads it.</summary>
    internal static int LiveCount;

    public int Width { get; }
    public int Height { get; }

    private Surface(IWICBitmap* bmp, int w, int h) { _bmp = bmp; Width = w; Height = h; Interlocked.Increment(ref LiveCount); }

    private static void EnsureFactories()
    {
        if (s_ready) return;
        lock (s_lock)
        {
            if (s_ready) return;
            Com.EnsureInitialized();
            IWICImagingFactory2* wic; var clsid = PInvoke.CLSID_WICImagingFactory2; var iid = typeof(IWICImagingFactory2).GUID;
            PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&wic).ThrowOnFailure();
            ID2D1Factory* d2d; var iidD2d = typeof(ID2D1Factory).GUID;
            PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, &iidD2d, null, (void**)&d2d).ThrowOnFailure();
            IDWriteFactory* dw; var iidDw = typeof(IDWriteFactory).GUID;
            PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iidDw, (void**)&dw).ThrowOnFailure();
            s_d2d = d2d; s_dw = dw; s_wic = wic;
            s_ready = true;   // published last: a thread that observes this true also observes the three pointers above
        }
    }

    // ---- construction --------------------------------------------------------------------

    public static Surface Create(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "surface must be at least 1x1");
        EnsureFactories();
        IWICBitmap* bmp; var pbgra = PInvoke.GUID_WICPixelFormat32bppPBGRA;
        s_wic->CreateBitmap((uint)width, (uint)height, &pbgra, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand, &bmp);
        return new Surface(bmp, width, height);
    }

    /// <summary>Any WIC-decodable image, converted to premultiplied BGRA.</summary>
    public static Surface Load(string path)
    {
        EnsureFactories();
        IWICBitmapDecoder* dec;
        fixed (char* p = path)
            dec = s_wic->CreateDecoderFromFilename(p, null, GENERIC_ACCESS_RIGHTS.GENERIC_READ, WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
        try
        {
            IWICBitmapFrameDecode* frame; dec->GetFrame(0, &frame);
            try
            {
                IWICFormatConverter* conv; s_wic->CreateFormatConverter(&conv);
                try
                {
                    var pbgra = PInvoke.GUID_WICPixelFormat32bppPBGRA;
                    conv->Initialize((IWICBitmapSource*)frame, &pbgra, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
                    IWICBitmap* bmp; s_wic->CreateBitmapFromSource((IWICBitmapSource*)conv, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, &bmp);
                    uint w, h; bmp->GetSize(&w, &h);
                    return new Surface(bmp, (int)w, (int)h);
                }
                finally { conv->Release(); }
            }
            finally { frame->Release(); }
        }
        finally { dec->Release(); }
    }

    /// <summary>Raw dump written by <see cref="SaveRaw"/>: 8-byte magic, int32 width, int32 height, then width*4*height bytes.</summary>
    public static Surface LoadRaw(string path)
    {
        using var fs = File.OpenRead(path);
        var header = new byte[16];
        fs.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual(RawMagic)) throw new InvalidDataException($"not a DeskWall raw surface: {path}");
        var w = BitConverter.ToInt32(header, 8); var h = BitConverter.ToInt32(header, 12);
        if (w <= 0 || h <= 0 || (long)w * h * 4 != fs.Length - 16) throw new InvalidDataException($"raw surface header does not match file length: {path}");
        var s = Create(w, h);
        var rowBytes = w * 4;
        var all = new byte[rowBytes * h];
        fs.ReadExactly(all);   // one read; the file is in the page cache on every tick after the first
        s.WithLock(LockWrite, new Rect(0, 0, w, h), (ptr, stride) =>
        {
            if (stride == rowBytes) Marshal.Copy(all, 0, ptr, all.Length);
            else for (var y = 0; y < h; y++) Marshal.Copy(all, y * rowBytes, (IntPtr)(ptr + y * stride), rowBytes);
        });
        return s;
    }

    // ---- persistence ---------------------------------------------------------------------

    public void SaveRaw(string path)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            {
                fs.Write(RawMagic); fs.Write(BitConverter.GetBytes(Width)); fs.Write(BitConverter.GetBytes(Height));
                var rowBytes = Width * 4;
                var all = new byte[rowBytes * Height];
                WithLock(LockRead, new Rect(0, 0, Width, Height), (ptr, stride) =>
                {
                    if (stride == rowBytes) Marshal.Copy(ptr, all, 0, all.Length);
                    else for (var y = 0; y < Height; y++) Marshal.Copy((IntPtr)(ptr + y * stride), all, y * rowBytes, rowBytes);
                });
                fs.Write(all);   // one write
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Finding 10: leave no <path>.tmp behind on a failing tick; this directory is
            // supposed to stay tidy across a daemon that ticks every minute for weeks.
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void SaveJpeg(string path, int quality) => Encode(path, PInvoke.GUID_ContainerFormatJpeg, Math.Clamp(quality, 1, 100) / 100f);

    public void SavePng(string path) => Encode(path, PInvoke.GUID_ContainerFormatPng, null);

    private void Encode(string path, Guid container, float? jpegQuality)
    {
        ReleaseRenderTarget();
        var tmp = path + ".tmp";
        try
        {
            IWICStream* stream; s_wic->CreateStream(&stream);
            try
            {
                fixed (char* p = tmp) stream->InitializeFromFilename(p, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE);
                IWICBitmapEncoder* enc = s_wic->CreateEncoder(&container, null);
                try
                {
                    enc->Initialize((IStream*)stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
                    IWICBitmapFrameEncode* frame; IPropertyBag2* bag; enc->CreateNewFrame(&frame, &bag);
                    try
                    {
                        if (jpegQuality is { } q)
                        {
                            fixed (char* pn = "ImageQuality")
                            {
                                var pb = new PROPBAG2 { pstrName = pn };
                                var v = new VARIANT();
                                v.Anonymous.Anonymous.vt = VARENUM.VT_R4;
                                v.Anonymous.Anonymous.Anonymous.fltVal = q;
                                bag->Write(1, &pb, &v);
                            }
                        }
                        frame->Initialize(bag);
                        frame->WriteSource((IWICBitmapSource*)_bmp, null);
                        frame->Commit();
                        enc->Commit();
                    }
                    finally { frame->Release(); bag->Release(); }
                }
                finally { enc->Release(); }
            }
            finally { stream->Release(); }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Finding 10: leave no <path>.tmp behind on a failing tick.
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // ---- pixels --------------------------------------------------------------------------

    /// <summary>Slow; tests only. Returns straight (un-premultiplied) ARGB.</summary>
    public (byte A, byte R, byte G, byte B) GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x));
        byte a = 0, r = 0, g = 0, b = 0;
        WithLock(LockRead, new Rect(x, y, 1, 1), (ptr, _) => { var p = (byte*)ptr; b = p[0]; g = p[1]; r = p[2]; a = p[3]; });
        if (a is > 0 and < 255) { r = (byte)Math.Min(255, r * 255 / a); g = (byte)Math.Min(255, g * 255 / a); b = (byte)Math.Min(255, b * 255 / a); }
        return (a, r, g, b);
    }

    /// <summary>Wrap a block of straight BGRA rows (top-down, tightly packed) as a surface. The caller
    /// must already have set alpha to 255 on opaque pixels: storage here is premultiplied.
    /// One lock, one copy; used by the screen capture path.</summary>
    internal static Surface FromBgra(int w, int h, ReadOnlySpan<byte> bgra)
    {
        var rowBytes = w * 4;
        if (bgra.Length < (long)rowBytes * h) throw new ArgumentException("bgra is shorter than width*height*4", nameof(bgra));
        var s = Create(w, h);
        fixed (byte* src = bgra)
        {
            var from = (IntPtr)src;
            s.WithLock(LockWrite, new Rect(0, 0, w, h), (ptr, stride) =>
            {
                for (var y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)from + (long)y * rowBytes, (byte*)ptr + (long)y * stride, rowBytes, rowBytes);
            });
        }
        return s;
    }

    /// <summary>Copy <paramref name="r"/> out as tightly packed BGRA rows. One lock, one copy; the
    /// calibrator's pixel comparison runs over the managed copy rather than through GetPixel.</summary>
    internal void ReadRegion(Rect r, byte[] dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        if (r.W <= 0 || r.H <= 0 || r.X < 0 || r.Y < 0 || r.Right > Width || r.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(r), "region is outside the surface");
        var rowBytes = r.W * 4;
        if (dst.Length < rowBytes * r.H) throw new ArgumentException("dst is shorter than the region", nameof(dst));
        WithLock(LockRead, r, (ptr, stride) =>
        {
            for (var y = 0; y < r.H; y++) Marshal.Copy((IntPtr)((byte*)ptr + (long)y * stride), dst, y * rowBytes, rowBytes);
        });
    }

    /// <summary>Replace the pixels of <paramref name="r"/> with the same rect from <paramref name="src"/>. Same-size surfaces.</summary>
    public void CopyRect(Surface src, Rect r)
    {
        if (src.Width != Width || src.Height != Height) throw new ArgumentException("CopyRect needs same-size surfaces");
        r = Clip(r); if (r.W <= 0 || r.H <= 0) return;
        var rowBytes = r.W * 4;
        var buf = new byte[rowBytes * r.H];
        src.WithLock(LockRead, r, (ptr, stride) => { for (var y = 0; y < r.H; y++) Marshal.Copy((IntPtr)(ptr + y * stride), buf, y * rowBytes, rowBytes); });
        WithLock(LockWrite, r, (ptr, stride) => { for (var y = 0; y < r.H; y++) Marshal.Copy(buf, y * rowBytes, (IntPtr)(ptr + y * stride), rowBytes); });
    }

    private void WithLock(uint flags, Rect r, Action<IntPtr, int> body)
    {
        ReleaseRenderTarget();   // D2D holds the bitmap while a target exists over it
        var wr = new WICRect { X = r.X, Y = r.Y, Width = r.W, Height = r.H };
        IWICBitmapLock* lk; _bmp->Lock(&wr, flags, &lk);
        try
        {
            uint stride; lk->GetStride(&stride);
            uint size; byte* data; lk->GetDataPointer(&size, &data);
            body((IntPtr)data, (int)stride);
        }
        finally { lk->Release(); }
    }

    // ---- drawing -------------------------------------------------------------------------

    private ID2D1RenderTarget* Rt()
    {
        if (_rt is null)
        {
            var props = new D2D1_RENDER_TARGET_PROPERTIES
            {
                type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96, dpiY = 96,
            };
            ID2D1RenderTarget* rt; s_d2d->CreateWicBitmapRenderTarget(_bmp, &props, &rt);
            rt->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
            _rt = rt;
        }
        return _rt;
    }

    private void ReleaseRenderTarget()
    {
        if (_rt is not null) { _rt->Release(); _rt = null; }
    }

    private delegate void DrawBody(ID2D1RenderTarget* rt);

    private void Draw(DrawBody body)
    {
        var rt = Rt();
        rt->BeginDraw();
        try
        {
            body(rt);
        }
        catch
        {
            // Finding 19: a half-pushed layer or clip left by the body must not poison the cached
            // target for every later Draw on this Surface, so drop it entirely rather than try to
            // rebalance it. EndDraw's own result is irrelevant here - call it (D2D expects a
            // matching EndDraw for every BeginDraw) but never let its failure mask the body's real
            // exception with a different one.
            rt->EndDraw(null, null);
            ReleaseRenderTarget();
            throw;
        }
        var hr = rt->EndDraw(null, null);
        if (hr.Failed)
        {
            ReleaseRenderTarget();   // do not leave a target that just failed EndDraw cached for reuse
            hr.ThrowOnFailure();
        }
    }

    public void Clear(Color c) => Draw(rt => { var cc = ToD2D(c); rt->Clear(&cc); });

    public void FillRect(Rect r, Color c, float radius = 0) => Draw(rt =>
    {
        var brush = Brush(rt, c);
        try
        {
            var rf = ToD2D(r);
            if (radius <= 0) rt->FillRectangle(&rf, (ID2D1Brush*)brush);
            else { var rr = new D2D1_ROUNDED_RECT { rect = rf, radiusX = radius, radiusY = radius }; rt->FillRoundedRectangle(&rr, (ID2D1Brush*)brush); }
        }
        finally { brush->Release(); }
    });

    public void DrawSurface(Surface src, Rect dst, Fit fit, float opacity = 1, float radius = 0) => Draw(rt =>
    {
        src.ReleaseRenderTarget();
        ID2D1Bitmap* bmp; rt->CreateBitmapFromWicBitmap((IWICBitmapSource*)src._bmp, null, &bmp);
        ID2D1RoundedRectangleGeometry* geo = null;
        try
        {
            var (srcRect, dstRect) = FitRects(src.Width, src.Height, dst, fit);
            if (radius > 0)
            {
                var rr = new D2D1_ROUNDED_RECT { rect = ToD2D(dst), radiusX = radius, radiusY = radius };
                s_d2d->CreateRoundedRectangleGeometry(&rr, &geo);
                var lp = new D2D1_LAYER_PARAMETERS
                {
                    contentBounds = new D2D_RECT_F { left = float.MinValue, top = float.MinValue, right = float.MaxValue, bottom = float.MaxValue },
                    geometricMask = (ID2D1Geometry*)geo,
                    maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                    maskTransform = Identity(),
                    opacity = 1f,
                    opacityBrush = null,
                    layerOptions = D2D1_LAYER_OPTIONS.D2D1_LAYER_OPTIONS_NONE,
                };
                rt->PushLayer(&lp, null);
            }
            rt->DrawBitmap(bmp, &dstRect, opacity, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, &srcRect);
            if (radius > 0) rt->PopLayer();
        }
        finally { bmp->Release(); if (geo is not null) geo->Release(); }
    });

    public void DrawText(string text, TextStyle style, Rect rect)
    {
        if (string.IsNullOrEmpty(text)) return;
        Draw(rt =>
        {
            // Glyph runs and the effect ring both paint outside the layout box, and the incremental
            // renderer only restores the base inside PaintBounds; clip to exactly that so nothing
            // can be painted that a later tick will not clean up. Margin is shared with
            // ResolvedText.PaintBounds through TextStyle.PaintMargin.
            var margin = style.PaintMargin();
            var clip = new D2D_RECT_F
            {
                left = rect.X - margin, top = rect.Y - margin,
                right = rect.Right + margin, bottom = rect.Bottom + margin,
            };
            rt->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            try
            {
                IDWriteTextFormat* fmt;
                fixed (char* fam = style.Font) fixed (char* loc = "en-GB")
                    s_dw->CreateTextFormat(fam, null, (DWRITE_FONT_WEIGHT)Math.Clamp(style.Weight, 1, 999), DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                        DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, style.Size, loc, &fmt);
                try
                {
                    fmt->SetTextAlignment(style.Align switch
                    {
                        Align.Right => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
                        Align.Center => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
                        _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
                    });
                    fmt->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
                    fmt->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);

                    IDWriteTextLayout* layout;
                    fixed (char* p = text) s_dw->CreateTextLayout(p, (uint)text.Length, fmt, rect.W, rect.H, &layout);
                    try
                    {
                        var origin = new D2D_POINT_2F { x = rect.X, y = rect.Y };
                        var main = Brush(rt, style.Color);
                        try
                        {
                            if (style.Effect != TextEffect.None && style.EffectColor.A > 0)
                            {
                                var eff = Brush(rt, style.EffectColor);
                                try
                                {
                                    switch (style.Effect)
                                    {
                                        case TextEffect.Plate:
                                            DWRITE_TEXT_METRICS m; layout->GetMetrics(&m);
                                            var plate = new D2D1_ROUNDED_RECT
                                            {
                                                rect = new D2D_RECT_F { left = rect.X + m.left - 8, top = rect.Y + m.top - 4, right = rect.X + m.left + m.width + 8, bottom = rect.Y + m.top + m.height + 4 },
                                                radiusX = style.EffectRadius, radiusY = style.EffectRadius,
                                            };
                                            rt->FillRoundedRectangle(&plate, (ID2D1Brush*)eff);
                                            break;
                                        case TextEffect.Outline:
                                            // Eight one-and-a-half-pixel offsets in the effect colour read as a stroke at text sizes.
                                            foreach (var (dx, dy) in Ring(1.5f))
                                            {
                                                var o = new D2D_POINT_2F { x = origin.x + dx, y = origin.y + dy };
                                                rt->DrawTextLayout(o, layout, (ID2D1Brush*)eff, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
                                            }
                                            break;
                                        default: // Shadow
                                            // Soft shadow approximated by stacking low-alpha copies in a ring of EffectRadius/2 around
                                            // a (1,1) offset. A true Gaussian effect needs ID2D1DeviceContext; see Phase 1 ledger ruling.
                                            var rad = Math.Max(1f, style.EffectRadius);
                                            var ring = Ring(rad / 2f);
                                            var soft = Brush(rt, style.EffectColor with { A = (byte)Math.Max(8, style.EffectColor.A / 4) });
                                            try
                                            {
                                                foreach (var (dx, dy) in ring)
                                                {
                                                    var o = new D2D_POINT_2F { x = origin.x + 1 + dx, y = origin.y + 1 + dy };
                                                    rt->DrawTextLayout(o, layout, (ID2D1Brush*)soft, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
                                                }
                                                var core = new D2D_POINT_2F { x = origin.x + 1, y = origin.y + 1 };
                                                rt->DrawTextLayout(core, layout, (ID2D1Brush*)eff, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
                                            }
                                            finally { soft->Release(); }
                                            break;
                                    }
                                }
                                finally { eff->Release(); }
                            }
                            rt->DrawTextLayout(origin, layout, (ID2D1Brush*)main, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
                        }
                        finally { main->Release(); }
                    }
                    finally { layout->Release(); }
                }
                finally { fmt->Release(); }
            }
            finally { rt->PopAxisAlignedClip(); }
        });
    }

    // ---- helpers -------------------------------------------------------------------------

    /// <summary>Cover: crop source to dst aspect. Contain: shrink dst to source aspect. Stretch: both full.</summary>
    internal static (D2D_RECT_F src, D2D_RECT_F dst) FitRects(int sw, int sh, Rect dst, Fit fit)
    {
        double sa = (double)sw / sh, da = (double)dst.W / dst.H;
        switch (fit)
        {
            case Fit.Stretch:
                return (new D2D_RECT_F { left = 0, top = 0, right = sw, bottom = sh }, ToD2D(dst));
            case Fit.Cover:
                if (sa > da) { var w = sh * da; var x = (sw - w) / 2; return (new D2D_RECT_F { left = (float)x, top = 0, right = (float)(x + w), bottom = sh }, ToD2D(dst)); }
                else { var h = sw / da; var y = (sh - h) / 2; return (new D2D_RECT_F { left = 0, top = (float)y, right = sw, bottom = (float)(y + h) }, ToD2D(dst)); }
            default:
                if (sa > da) { var h = dst.W / sa; var y = dst.Y + (dst.H - h) / 2; return (new D2D_RECT_F { left = 0, top = 0, right = sw, bottom = sh }, new D2D_RECT_F { left = dst.X, top = (float)y, right = dst.Right, bottom = (float)(y + h) }); }
                else { var w = dst.H * sa; var x = dst.X + (dst.W - w) / 2; return (new D2D_RECT_F { left = 0, top = 0, right = sw, bottom = sh }, new D2D_RECT_F { left = (float)x, top = dst.Y, right = (float)(x + w), bottom = dst.Bottom }); }
        }
    }

    private static IEnumerable<(float dx, float dy)> Ring(float r)
    {
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            yield return ((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));
        }
    }

    private static ID2D1SolidColorBrush* Brush(ID2D1RenderTarget* rt, Color c)
    {
        var cc = ToD2D(c);
        ID2D1SolidColorBrush* brush; rt->CreateSolidColorBrush(&cc, null, &brush);
        return brush;
    }

    private Rect Clip(Rect r)
    {
        var x = Math.Max(0, r.X); var y = Math.Max(0, r.Y);
        return new Rect(x, y, Math.Min(Width, r.Right) - x, Math.Min(Height, r.Bottom) - y);
    }

    private static D2D_MATRIX_3X2_F Identity()
    {
        var m = new D2D_MATRIX_3X2_F();
        m.Anonymous.Anonymous1.m11 = 1f;
        m.Anonymous.Anonymous1.m22 = 1f;
        return m;
    }

    private static D2D1_COLOR_F ToD2D(Color c) => new() { r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f };
    private static D2D_RECT_F ToD2D(Rect r) => new() { left = r.X, top = r.Y, right = r.Right, bottom = r.Bottom };

    public void Dispose()
    {
        ReleaseRenderTarget();
        if (_bmp is not null) { _bmp->Release(); _bmp = null; Interlocked.Decrement(ref LiveCount); }
        GC.SuppressFinalize(this);
    }

#if DEBUG
    // Finding 22: releasing COM interfaces from the finalizer thread is wrong - it never ran
    // Com.EnsureInitialized and has no ordering relationship with the process-wide factories, and
    // every production call site already uses `using`. Keep only a leak assertion in DEBUG builds,
    // which touches no COM pointer.
    ~Surface()
    {
        if (_bmp is not null) System.Diagnostics.Debug.Fail("Surface leaked");
    }
#endif
}
