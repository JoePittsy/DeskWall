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

Path geometry, added 2026-09-21 for the `dial` component (`Surface.DrawArc`). Declared in
`NativeMethods.txt`: `ID2D1PathGeometry`, `ID2D1GeometrySink`, `ID2D1SimplifiedGeometrySink`,
`D2D1_ARC_SEGMENT`, `D2D1_SWEEP_DIRECTION`, `D2D1_ARC_SIZE`, `D2D1_FIGURE_BEGIN`,
`D2D1_FIGURE_END`, `D2D_SIZE_F`, `D2D_POINT_2F`. Shapes that compiled:

```
ID2D1Factory.CreatePathGeometry(ID2D1PathGeometry**)
ID2D1PathGeometry.Open(ID2D1GeometrySink**)
ID2D1GeometrySink.AddArc(D2D1_ARC_SEGMENT*)                      // by pointer
ID2D1GeometrySink.BeginFigure(D2D_POINT_2F, D2D1_FIGURE_BEGIN)   // start point BY VALUE
ID2D1GeometrySink.EndFigure(D2D1_FIGURE_END)
ID2D1GeometrySink.Close()
ID2D1RenderTarget.DrawGeometry(ID2D1Geometry*, ID2D1Brush*, float strokeWidth, ID2D1StrokeStyle*)
```

Confirmed: with `allowMarshaling: false` the generator copies the base interface's methods onto
the derived struct, so `BeginFigure`/`EndFigure`/`Close` (declared on
`ID2D1SimplifiedGeometrySink`) are callable straight off an `ID2D1GeometrySink*` with no cast.
Measured: `ID2D1SimplifiedGeometrySink` does **not** have to be listed for that to work -
`ID2D1GeometrySink` pulls its base in on its own, and the build is clean without the line. It is
listed anyway, for the reader. `ID2D1StrokeStyle` needs no declaration either: passing `null` for
the default flat-capped stroke compiles without it. A sweep of 360 or more is emitted as two
segments because one `D2D1_ARC_SEGMENT` whose end point is its start point describes no arc; the
`dial-states` golden shows the resulting ring closed.

## Task 10 measurements

JIT Release `deskwall.exe tick --measure`, layout `layouts/clock-disks.json` (clock + 3 drive rows = 7
components), photo base, each run a fresh process (so every run pays JIT warm-up of the render path).

| run | resolve | draw | encode | apply | total wall | cpu |
|---|---|---|---|---|---|---|
| first ever (builds base cache from the 4K Spotlight JPEG) | 19 | 152 | 18 | 7 | 206 | 203 |
| force, base cached | 30 | 60 | 19 | 5 | 119 | 109 |
| same minute, nothing changed | 26 | 0 | 0 | 0 | 26 SKIPPED | 31 |
| same minute again | 27 | 0 | 0 | 0 | 27 SKIPPED | 31 |

Findings:
- The skip path works only after quantising `ResolvedBar`'s fraction to 0.1 percent in its content
  key; raw free-space wobble between reads changed the key every tick (fixed in Resolve/Resolved.cs).
- `resolve` at ~26 ms is almost entirely JIT compilation plus `Monitors.Enumerate` + drive enumeration
  in a cold process; under a resident AOT daemon this is expected to be low single-digit ms. The
  60 ms/40 ms budget cannot be judged until AOT publish works (MSVC linker missing).
- `apply` varies 5 to 71 ms: that is Explorer's side of `IDesktopWallpaper::SetWallpaper`, not ours.
- Output JPEG q92 with the photo base is ~1.9 MB, in line with the POC.

## Task 11 measurements

Same setup as Task 10 (JIT Release, fresh process per run, display at that moment: RDP session
1920x1200, so the layout's components were off-canvas; costs are still representative).

| run | resolve | draw | encode | apply | total wall | cpu | redrawn |
|---|---|---|---|---|---|---|---|
| force (full render) | 24 | 45 | 17 | 4 | 95 | 109 | 7 |
| minute changed (incremental) | 24 | 54 | 18 | 5 | 106 | 94 | 3 |
| same minute (skip) | 24 | 0 | 0 | 0 | 24 | 31 | 0 |

Before optimisation the incremental path measured draw 177 ms: `LoadRaw`/`SaveRaw` did 1200
row-sized file operations each and the incremental renderer copied the whole previous frame into
a fresh surface. Now raw I/O is one read and one write, and `RenderIncremental` mutates the
previous frame in place. In a cold JIT process the incremental path still pays for loading two
raw frames (previous + base, 9 MB each at 1920x1200; 20 MB each at 3440x1440) against one for the
full render, which is why it is not yet faster. The resident AOT daemon (Phase 2) is where the
comparison matters: the OS page cache holds both files and no JIT is paid. Re-measure there.

Design note for Phase 2: the previous frame must NOT be kept in memory in the daemon. At
3440x1440 it is 19.8 MB, which alone breaks the 10 MB idle budget. Loading it from disk per tick
is the design, and the single-read `LoadRaw` is what makes that cheap.

## Phase 6 budget results

Spec 1.2 measured against the published native-AOT daemon by
`tests/DeskWall.Core.Tests/Budget/BudgetTests.cs`:

```
dotnet publish src/DeskWall.Daemon -c Release -r win-x64
dotnet test tests/DeskWall.Core.Tests --filter Category=Budget
```

Each test prints its own row; paste them in below. A row over budget is a finding for the
controller, never a reason to raise the budget.

First run 2026-09-21 on JOES-PC, native AOT `deskwall.exe` 6.54 MB (ILCompiler 10.0.11, MSVC
14.51, Windows SDK 10.0.26100), 3440x1440 primary, POC task disabled for the run, session was an
RDP session at the console's native resolution:

| Test | Measured | Budget | Verdict |
|---|---|---|---|
| `ColdStart_To_First_Wallpaper` | 110 ms | < 500 ms | OK |
| `Idle_PrivateBytes_After_Trim` | 48.42 MB (working set 1.55 MB) | < 10 MB | OVER |
| `Idle_Cpu_Between_Wakes` | 0 ms (344 ms total over 240 s, all of it inside ticks) | < 50 ms | OK |
| `Idle_Handles_And_Threads` | 279 h / 9 t | < 100 h / < 5 t | OVER |
| `ClockOnly_Tick_Wall_And_Cpu` | 146 ms wall / 78 ms cpu (resolve 1, draw 118, encode 25) | < 60 ms wall / < 40 ms cpu | OVER |

Second run, same day, after the memory fix wave (raw frame streamed straight between file and
locked bitmap with no managed staging array; `Footprint.Release()` after every tick, an
aggressive compacting gen2 collection before the working-set trim):

| Test | Measured | Budget | Verdict |
|---|---|---|---|
| `ColdStart_To_First_Wallpaper` | 99 ms | < 500 ms | OK |
| `Idle_PrivateBytes_After_Trim` | 8.22 MB (working set 0.64 MB) | < 10 MB | OK |
| `Idle_Cpu_Between_Wakes` | 0 ms (172 ms total over 240 s, all of it inside ticks) | < 50 ms | OK |
| `Idle_Handles_And_Threads` | 279 h / 9 t | < 100 h / < 5 t | OVER |
| `ClockOnly_Tick_Wall_And_Cpu` | 92 ms wall / 62 ms cpu (resolve 1, draw 65, encode 24) | < 60 ms wall / < 40 ms cpu | OVER |

Removing the two 19.8 MB managed copies per tick took the clock-only draw stage from 118 to
65 ms on its own. Handles and threads did not move, as expected: they are there before the
first tick.

Notes on the findings from the first run:

- The `cpu` figure is `Environment.CpuUsage` deltas, which move in 15.6 ms scheduler quanta:
  every non-skipped one-shot tick read exactly 78 ms (5 quanta) and every skipped tick 16 ms, so
  treat it as "between 63 and 94 ms", not 78.
- A one-shot `tick` pays factory, font and render-target setup in `draw`: three forced full
  redraws of all 7 components measured draw 88/305/109 ms and three clock-only incremental ticks
  (1 component, via `frame.raw`) measured draw 117/110/105 ms. Redrawing one component is not
  cheaper than redrawing seven in a fresh process; the previous-frame read and write (18.9 MB
  each way) roughly cancels the drawing saved.
- Private bytes is commit, not working set: the working set after `Footprint.Trim()` was 1.55 MB.
  The commit is the GC heap left committed after the 19.8 MB frame buffers of the last tick.
- Handles and threads on the resident daemon, sampled from outside (`run --no-tray
  --no-shortcuts`, scratch home, real wallpaper applied): 257 handles / 17 threads two seconds
  after start, before any tick, so the bulk is the runtime and the factories, not per-tick
  leakage; 261 handles after every one of four ticks, threads settling 17 -> 13 -> 10 as the
  runtime's startup threads exit. Private bytes at the same samples: 78.7, 48.2, 66.8, 48.5,
  66.9 MB; working set 0.6-1.6 MB throughout.
- The resident daemon's own log for those ticks (wall includes `IDesktopWallpaper::SetWallpaper`,
  which `--no-apply` in the suite leaves out): `start: redrawn 7 total 154 ms cpu 109 ms`, then
  four clock-only Timer ticks at `165/94`, `130/62`, `170/109`, `76/47` ms wall/cpu. The one-shot
  comparison above was running during the middle three; the undisturbed last one is the best
  single number for a warm resident clock-only tick so far, and it is still over 60/40.
