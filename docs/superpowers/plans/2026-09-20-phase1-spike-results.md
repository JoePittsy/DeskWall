# Phase 1 spike results: CsWin32 + software Direct2D + WIC + IDesktopWallpaper

Date: 2026-09-20. Machine: JOES-PC (i7-6700K, 3440x1440). Runtime: **JIT Release**, not
native AOT. Native AOT publish is blocked until the Visual Studio "Desktop development with
C++" workload (MSVC linker + Windows SDK) is installed; VS 2026 Community 18.9 is present
without it. Re-run under AOT when available and append below.

## Timings (3440x1440 frame, one text run, JPEG q92, wallpaper applied)

| run | draw | encode | apply | total wall | cpu | working set (JIT) | private (JIT) |
|---|---|---|---|---|---|---|---|
| 1 | 17 ms | 20 ms | 8 ms | 72 ms | 62 ms | 56 MB | 28 MB |
| 2 | 11 ms | 19 ms | 7 ms | 61 ms | 62 ms | 56 MB | 28 MB |
| 3 | 11 ms | 19 ms | 7 ms | 63 ms | 62 ms | 56 MB | 28 MB |

"total" includes creating the WIC, D2D and DirectWrite factories and the wallpaper COM
server round trip. Encode of a flat frame produced 79 KB; a photo will be ~2 MB and encode
time will rise somewhat. The working-set and private numbers are the JIT runtime's and are
not meaningful for the AOT budget.

**Decision (plan Task 1 Step 4):** encode is 19 ms, well under 40 ms, so Task 11
(incremental redraw) is not mandatory for the budget. It is still implemented because the
spec requires it, and measured, not assumed.

## Modules loaded

`d3d11.dll`, `dxgi.dll`, `D3D10Warp.dll`. **No vendor GPU driver** (no nv*, amd*, ig*).

Direct2D's `D2D1_RENDER_TARGET_TYPE_SOFTWARE` is implemented on WARP, Microsoft's software
D3D rasteriser, so a software D3D device does exist in-process. What the spec actually cares
about holds: no hardware GPU device, no vendor user-mode driver DLL in the process, "GPU not
used" in the tooltip is true. Spec section 1.1 wording "No D3D device is ever created" is
amended to "no hardware D3D device; Direct2D renders through WARP in software". Ruling
recorded in the Phase 1 ledger.

## CsWin32 shapes verified (allowMarshaling: false)

- COM interfaces are `struct`s addressed by pointer; **methods return `void` and throw a
  `COMException` on failure**, except a few (`EndDraw`) that return `HRESULT`. Do not write
  `.ThrowOnFailure()` on void methods.
- Static entry points return `HRESULT`; `.ThrowOnFailure()` applies to those.
- `PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED)` returns
  `0x80010106 RPC_E_CHANGED_MODE` in a .NET process whose main thread is already MTA. Treat
  `S_FALSE` and `RPC_E_CHANGED_MODE` as success (COM is up).
- Namespaces: `IWICImagingFactory2` is in `Windows.Win32.Graphics.Imaging.D2D`;
  `IPropertyBag2`/`PROPBAG2` in `Windows.Win32.System.Com.StructuredStorage`; `VARIANT`,
  `VARENUM` in `Windows.Win32.System.Variant`; `GENERIC_WRITE` is
  `GENERIC_ACCESS_RIGHTS.GENERIC_WRITE` (list `GENERIC_ACCESS_RIGHTS`, not `GENERIC_WRITE`).
- `GetLastError` is refused by the generator (use `Marshal.GetLastWin32Error`).
- Project must target `net10.0-windows10.0.19041.0` (TargetPlatformVersion) or CA1416 fires
  as an error for Windows 8+ APIs. `Directory.Build.props` now does this for the whole repo.

### Exact calls used

```csharp
// factories
IWICImagingFactory2* wic; var clsid = PInvoke.CLSID_WICImagingFactory2; var iid = typeof(IWICImagingFactory2).GUID;
PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&wic).ThrowOnFailure();
ID2D1Factory* d2d; var iidD2d = typeof(ID2D1Factory).GUID;
PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iidD2d, null, (void**)&d2d).ThrowOnFailure();
IDWriteFactory* dw; var iidDw = typeof(IDWriteFactory).GUID;
PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iidDw, (void**)&dw).ThrowOnFailure();

// bitmap + render target
IWICBitmap* bmp; var pbgra = PInvoke.GUID_WICPixelFormat32bppPBGRA;
wic->CreateBitmap((uint)W, (uint)H, &pbgra, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand, &bmp);
var props = new D2D1_RENDER_TARGET_PROPERTIES {
    type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
    pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
    dpiX = 96, dpiY = 96 };
ID2D1RenderTarget* rt; d2d->CreateWicBitmapRenderTarget(bmp, &props, &rt);

// drawing
rt->BeginDraw();
var bg = new D2D1_COLOR_F { r = .., g = .., b = .., a = 1f }; rt->Clear(&bg);
ID2D1SolidColorBrush* brush; rt->CreateSolidColorBrush(&color, null, &brush);
IDWriteTextFormat* fmt;
fixed (char* fam = family) fixed (char* loc = "en-GB")
    dw->CreateTextFormat(fam, null, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_LIGHT, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size, loc, &fmt);
fmt->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);
var rect = new D2D_RECT_F { left = .., top = .., right = .., bottom = .. };
fixed (char* p = text)
    rt->DrawText(p, (uint)text.Length, fmt, &rect, (ID2D1Brush*)brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
rt->EndDraw(null, null).ThrowOnFailure();

// JPEG encode with quality
IWICStream* stream; wic->CreateStream(&stream);
fixed (char* op = path) stream->InitializeFromFilename(op, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE);
var jpeg = PInvoke.GUID_ContainerFormatJpeg;
IWICBitmapEncoder* enc = wic->CreateEncoder(&jpeg, null);          // returns the pointer
enc->Initialize((IStream*)stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
IWICBitmapFrameEncode* frame; IPropertyBag2* bag; enc->CreateNewFrame(&frame, &bag);
fixed (char* pn = "ImageQuality") {
    var pb = new PROPBAG2 { pstrName = pn };
    var v = new VARIANT(); v.Anonymous.Anonymous.vt = VARENUM.VT_R4; v.Anonymous.Anonymous.Anonymous.fltVal = quality / 100f;
    bag->Write(1, &pb, &v);
}
frame->Initialize(bag); frame->WriteSource((IWICBitmapSource*)bmp, null); frame->Commit(); enc->Commit();

// wallpaper
IDesktopWallpaper* dwp; var c = typeof(DesktopWallpaper).GUID; var i = typeof(IDesktopWallpaper).GUID;
PInvoke.CoCreateInstance(&c, null, CLSCTX.CLSCTX_LOCAL_SERVER, &i, (void**)&dwp).ThrowOnFailure();
fixed (char* op = path) dwp->SetWallpaper(null, op);                 // null = every monitor
```

Other signatures seen in the generated code and needed by Task 7/8:

```
IDesktopWallpaper: GetMonitorDevicePathCount(uint*), GetMonitorDevicePathAt(uint, PWSTR*),
                   GetMonitorRECT(PCWSTR, RECT*), GetWallpaper(PCWSTR, PWSTR*)   (all void, throw)
IWICImagingFactory: CreateDecoderFromFilename(PCWSTR, Guid*, GENERIC_ACCESS_RIGHTS, WICDecodeOptions) -> IWICBitmapDecoder*
                    CreateFormatConverter(IWICFormatConverter**), CreateBitmapFromSource(IWICBitmapSource*, WICBitmapCreateCacheOption, IWICBitmap**)
IWICBitmapDecoder.GetFrame(uint, IWICBitmapFrameDecode**)
IWICFormatConverter.Initialize(IWICBitmapSource*, Guid* dstFormat, WICBitmapDitherType, IWICPalette*, double, WICBitmapPaletteType)
IWICBitmap.Lock(WICRect*, uint flags, IWICBitmapLock**)
ID2D1RenderTarget.CreateBitmapFromWicBitmap(IWICBitmapSource*, D2D1_BITMAP_PROPERTIES*, ID2D1Bitmap**)
ID2D1RenderTarget.DrawBitmap(ID2D1Bitmap*, D2D_RECT_F* dst, float opacity, D2D1_BITMAP_INTERPOLATION_MODE, D2D_RECT_F* src)
```

Note for Task 7: `GetMonitorRECT` throws (does not return a failing HRESULT) for a detached
monitor id; wrap in try/catch `COMException` and skip.

## Task 10 measurements

(pending)

## Task 11 measurements

(pending)
