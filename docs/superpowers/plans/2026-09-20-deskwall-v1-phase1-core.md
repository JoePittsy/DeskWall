# DeskWall v1 Phase 1: Core Model, Renderer and `tick` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A native-AOT `deskwall.exe tick` that reads a hand-written layout JSON, resolves
bindings against the `time` and `disks` sources, renders with software Direct2D, encodes a
JPEG with WIC, sets it as the wallpaper, and prints per-stage timings.

**Architecture:** `DeskWall.Core` holds a typed value tree, a binding parser and resolver, a
polymorphic layout model, an `ISource` seam, a resolver that expands repeaters into concrete
resolved components with content keys, and a `Surface` wrapper over Direct2D/WIC. The daemon
project is only a command-line entry point in this phase.

**Tech Stack:** .NET 10, `PublishAot`, `Microsoft.Windows.CsWin32` with `allowMarshaling: false`,
System.Text.Json source generation, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md` (sections 2, 4, 5, 6)
**Master plan:** `docs/superpowers/plans/2026-09-20-deskwall-v1-master.md`

## Global Constraints

- .NET 10 LTS. `PublishAot=true` on Core and Daemon; IL2xxx/IL3xxx warnings are errors.
- No GPU. `D2D1_RENDER_TARGET_TYPE_SOFTWARE` only. No D3D11 anywhere in the solution.
- NuGet allowed: `Microsoft.Windows.CsWin32` (PrivateAssets all), `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.
- Budget for the clock-only tick after Task 10: 60 ms wall, 40 ms CPU on JOES-PC.
- Sources ASCII only. Runtime dir is `%LOCALAPPDATA%\DeskWall`.
- Lane order (master plan): Tasks 0, 1, 2 sequential by the seam owner. Then lanes
  `v1/p1-binding` (Tasks 3, 4), `v1/p1-sources` (Tasks 5, 7), `v1/p1-render` (Tasks 8, 9).
  Then Tasks 6, 10, 11 by the integrator on `v1`.

## File structure

```
DeskWall.sln
Directory.Build.props                       LangVersion, Nullable, AOT warnings as errors
src/DeskWall.Core/DeskWall.Core.csproj
src/DeskWall.Core/NativeMethods.txt         CsWin32 surface
src/DeskWall.Core/NativeMethods.json        { "allowMarshaling": false }
src/DeskWall.Core/Values/Value.cs           Value hierarchy (Task 2)
src/DeskWall.Core/Values/ValueTree.cs       root record helpers (Task 2)
src/DeskWall.Core/Binding/Binding.cs        Binding, PathSegment (Task 3)
src/DeskWall.Core/Binding/BindingParser.cs  (Task 3)
src/DeskWall.Core/Binding/BindingResolver.cs (Task 3)
src/DeskWall.Core/Layout/PropertyValue.cs   literal-or-binding + JSON converter (Task 4)
src/DeskWall.Core/Layout/ComponentDef.cs    polymorphic defs (Task 4)
src/DeskWall.Core/Layout/SourceDef.cs       (Task 4)
src/DeskWall.Core/Layout/LayoutFile.cs      root + LayoutJsonContext (Task 4)
src/DeskWall.Core/Sources/ISource.cs        seam (Task 2)
src/DeskWall.Core/Sources/SourceSnapshot.cs (Task 2)
src/DeskWall.Core/Sources/TimeSource.cs     (Task 5)
src/DeskWall.Core/Sources/DisksSource.cs    (Task 5)
src/DeskWall.Core/Sources/SourceFactory.cs  name -> ISource (Task 5)
src/DeskWall.Core/Resolve/Resolved.cs       ResolvedText/Image/Bar/Shortcut (Task 2)
src/DeskWall.Core/Resolve/LayoutResolver.cs (Task 6)
src/DeskWall.Core/Resolve/ContentKey.cs     (Task 6)
src/DeskWall.Core/Display/DisplaySignature.cs (Task 7)
src/DeskWall.Core/Display/Monitors.cs       (Task 7)
src/DeskWall.Core/Render/Color.cs           (Task 2)
src/DeskWall.Core/Render/TextStyle.cs       (Task 2)
src/DeskWall.Core/Render/Surface.cs         D2D/WIC wrapper (Task 1 spike -> Task 8)
src/DeskWall.Core/Render/FrameRenderer.cs   (Task 8)
src/DeskWall.Core/Render/BaseCache.cs       (Task 8)
src/DeskWall.Core/Wallpaper/WallpaperSetter.cs (Task 9)
src/DeskWall.Core/Tick/TickRunner.cs        (Task 10)
src/DeskWall.Core/Tick/TickTimings.cs       (Task 10)
src/DeskWall.Core/Paths.cs                  runtime dir (Task 0)
src/DeskWall.Daemon/DeskWall.Daemon.csproj  WinExe, PublishAot
src/DeskWall.Daemon/Program.cs              subcommand dispatch (Task 0, Task 10)
tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj
tests/DeskWall.Core.Tests/**/*Tests.cs
spikes/D2dWic/                              Task 1, deleted at end of Task 1
layouts/clock-disks.json                    hand-written layout (Task 10)
poc/                                        the POC, moved in Task 0
```

---

### Task 0: Branch, move the POC, scaffold the solution

**Files:**
- Create: `DeskWall.sln`, `Directory.Build.props`, `src/DeskWall.Core/DeskWall.Core.csproj`,
  `src/DeskWall.Daemon/DeskWall.Daemon.csproj`, `src/DeskWall.Daemon/Program.cs`,
  `tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj`, `src/DeskWall.Core/Paths.cs`,
  `tests/DeskWall.Core.Tests/PathsTests.cs`
- Move: every `*.ps1`, `*.vbs`, `*.cs`, `widgets/` from the root into `poc/`
- Modify: `.gitignore`, `README.md` (one status line), `CLAUDE.md` (one pointer line)

**Interfaces:**
- Produces: `DeskWall.Core.Paths.RuntimeDir` (string, `%LOCALAPPDATA%\DeskWall`, created on first access).

- [ ] **Step 1: Create the integration branch and move the POC**

```bash
git checkout -b v1
mkdir poc
git mv compose.ps1 data.ps1 shortcuts.ps1 verify.ps1 install-task.ps1 tick.vbs DeskIcons.cs widgets poc/
```

Then edit `poc/install-task.ps1` is NOT needed: the scheduled task points at the old path.
Re-register it so the POC keeps ticking during the rewrite:

```powershell
powershell -File .\poc\install-task.ps1
```

Expected output contains `registered 'DeskWall Tick'`.

- [ ] **Step 2: Scope .gitignore to runtime output**

Replace `.gitignore` with:

```
# runtime output belongs in %LOCALAPPDATA%\DeskWall, never here
bin/
obj/
*.user
.vs/
state.json
restore.txt
*.backup.json
*.log
# goldens under tests/ are tracked; stray renders elsewhere are not
/*.png
/*.jpg
/*.ico
poc/*.png
poc/*.ico
```

- [ ] **Step 3: Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsAotCompatible>true</IsAotCompatible>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Core project**

`src/DeskWall.Core/DeskWall.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>DeskWall.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.*" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

`src/DeskWall.Core/NativeMethods.json`:

```json
{ "$schema": "https://aka.ms/CsWin32.schema.json", "allowMarshaling": false, "useSafeHandles": true }
```

`src/DeskWall.Core/NativeMethods.txt` (grows in later tasks):

```
GetLastError
```

`src/DeskWall.Core/Paths.cs`:

```csharp
namespace DeskWall.Core;

public static class Paths
{
    private static string? _runtimeDir;

    /// <summary>%LOCALAPPDATA%\DeskWall, created on first access.</summary>
    public static string RuntimeDir
    {
        get
        {
            if (_runtimeDir is null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWall");
                Directory.CreateDirectory(dir);
                _runtimeDir = dir;
            }
            return _runtimeDir;
        }
    }

    public static string InRuntime(params string[] parts) => Path.Combine([RuntimeDir, .. parts]);
}
```

- [ ] **Step 5: Daemon project**

`src/DeskWall.Daemon/DeskWall.Daemon.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <AssemblyName>deskwall</AssemblyName>
    <RootNamespace>DeskWall.Daemon</RootNamespace>
    <PublishAot>true</PublishAot>
    <OptimizationPreference>Size</OptimizationPreference>
    <StackTraceSupport>false</StackTraceSupport>
    <UseSystemResourceKeys>true</UseSystemResourceKeys>
    <DisableImplicitNamespaceImports>false</DisableImplicitNamespaceImports>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DeskWall.Core\DeskWall.Core.csproj" />
  </ItemGroup>
</Project>
```

`src/DeskWall.Daemon/app.manifest` (per-monitor DPI aware so pixel coordinates are physical):

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/DeskWall.Daemon/Program.cs`:

```csharp
using DeskWall.Core;

namespace DeskWall.Daemon;

internal static class Program
{
    private static int Main(string[] argv)
    {
        var cmd = argv.Length == 0 ? "run" : argv[0];
        switch (cmd)
        {
            case "paths":
                Console.WriteLine(Paths.RuntimeDir);
                return 0;
            default:
                Console.Error.WriteLine($"deskwall: unknown or not yet implemented command '{cmd}'");
                return 2;
        }
    }
}
```

(`WinExe` has no console; `Console.WriteLine` is still valid and goes nowhere unless a
parent console is attached. Task 10 attaches to the parent console for `tick`.)

- [ ] **Step 6: Test project and the first test**

`tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsAotCompatible>false</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\DeskWall.Core\DeskWall.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/DeskWall.Core.Tests/PathsTests.cs`:

```csharp
using DeskWall.Core;
using Xunit;

public class PathsTests
{
    [Fact]
    public void RuntimeDir_IsUnderLocalAppData_AndExists()
    {
        var dir = Paths.RuntimeDir;
        Assert.EndsWith(@"\DeskWall", dir);
        Assert.True(Directory.Exists(dir));
    }
}
```

- [ ] **Step 7: Solution, build, test, publish**

```bash
dotnet new sln -n DeskWall
dotnet sln add src/DeskWall.Core src/DeskWall.Daemon tests/DeskWall.Core.Tests
dotnet build
dotnet test
dotnet publish src/DeskWall.Daemon -c Release -r win-x64
```

Expected: build and test green; publish produces `deskwall.exe` under
`src/DeskWall.Daemon/bin/Release/net10.0-windows/win-x64/publish/` with no IL warnings.
Run `.\...\publish\deskwall.exe paths` from a console: prints the runtime dir.

- [ ] **Step 8: Docs pointers and commit**

Add to the top of `README.md` under the title:

```
**Status:** v1 rewrite in progress on branch `v1`; see `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md`.
The PowerShell proof of concept lives in `poc/` and still runs the desktop until parity.
```

Add to `CLAUDE.md` after the first paragraph:

```
**v1 rewrite:** spec in `docs/superpowers/specs/`, plans in `docs/superpowers/plans/`. The POC
described below now lives in `poc/`; paths in this file are relative to that folder.
```

```bash
git add -A
git commit -m "v1 scaffold: move POC to poc/, add Core/Daemon/Tests projects"
```

---

### Task 1: Spike CsWin32 + software Direct2D + WIC encode + IDesktopWallpaper

**Files:**
- Create: `spikes/D2dWic/D2dWic.csproj`, `spikes/D2dWic/NativeMethods.txt`,
  `spikes/D2dWic/NativeMethods.json`, `spikes/D2dWic/Program.cs`
- Produce: `docs/superpowers/plans/2026-09-20-phase1-spike-results.md`
- Delete at the end: `spikes/`

This is a throwaway. Its output is the results file and the exact CsWin32 call shapes, which
Task 8 copies into `Surface.cs`. Do not polish it.

**Interfaces:**
- Produces: verified signatures for `D2D1CreateFactory`, `CoCreateInstance(CLSID_WICImagingFactory2)`,
  `IWICImagingFactory2.CreateBitmap`, `ID2D1Factory.CreateWicBitmapRenderTarget`,
  `DWriteCreateFactory`, `IDWriteFactory.CreateTextFormat`, `ID2D1RenderTarget.DrawText`,
  `IWICImagingFactory.CreateEncoder` (JPEG), `IDesktopWallpaper.SetWallpaper`.

- [ ] **Step 1: Project and native surface**

`spikes/D2dWic/D2dWic.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.*" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

`spikes/D2dWic/NativeMethods.json`: same as Core.

`spikes/D2dWic/NativeMethods.txt`:

```
CoInitializeEx
CoCreateInstance
CoUninitialize
D2D1CreateFactory
ID2D1Factory
ID2D1RenderTarget
ID2D1SolidColorBrush
ID2D1Bitmap
D2D1_RENDER_TARGET_PROPERTIES
D2D1_RENDER_TARGET_TYPE
D2D1_PIXEL_FORMAT
D2D1_FACTORY_TYPE
D2D1_FACTORY_OPTIONS
D2D_RECT_F
D2D1_COLOR_F
D2D1_BITMAP_INTERPOLATION_MODE
D2D1_DRAW_TEXT_OPTIONS
DWriteCreateFactory
IDWriteFactory
IDWriteTextFormat
DWRITE_FACTORY_TYPE
DWRITE_FONT_WEIGHT
DWRITE_FONT_STYLE
DWRITE_FONT_STRETCH
DWRITE_TEXT_ALIGNMENT
DWRITE_MEASURING_MODE
IWICImagingFactory2
IWICBitmap
IWICBitmapEncoder
IWICBitmapFrameEncode
IWICBitmapDecoder
IWICBitmapFrameDecode
IWICFormatConverter
IWICStream
CLSID_WICImagingFactory2
GUID_WICPixelFormat32bppPBGRA
GUID_ContainerFormatJpeg
GUID_ContainerFormatPng
WICBitmapCreateCacheOption
WICDecodeOptions
WICBitmapEncoderCacheOption
WICBitmapDitherType
WICBitmapPaletteType
IPropertyBag2
PROPBAG2
IDesktopWallpaper
DesktopWallpaper
DXGI_FORMAT
D2D1_ALPHA_MODE
```

Build once (`dotnet build spikes/D2dWic`) and fix the list against the errors CsWin32
reports for names it does not know. Record every rename in the results file.

- [ ] **Step 2: Program.cs**

The shape below is what CsWin32 with `allowMarshaling: false` typically generates: COM
interfaces are structs addressed by pointer, `HRESULT` returns with `.ThrowOnFailure()`.
Adjust to what the generator actually emitted and record the differences.

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

unsafe
{
    const int W = 3440, H = 1440;
    var proc = Process.GetCurrentProcess();
    var cpu0 = proc.TotalProcessorTime;
    var sw = Stopwatch.StartNew();

    PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED).ThrowOnFailure();

    // WIC factory
    IWICImagingFactory2* wic;
    PInvoke.CoCreateInstance(PInvoke.CLSID_WICImagingFactory2, null, CLSCTX.CLSCTX_INPROC_SERVER, out wic).ThrowOnFailure();

    // 32bppPBGRA bitmap the size of the canvas
    IWICBitmap* bmp;
    wic->CreateBitmap(W, H, PInvoke.GUID_WICPixelFormat32bppPBGRA, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand, &bmp).ThrowOnFailure();

    // D2D software render target over the WIC bitmap. No D3D device exists.
    ID2D1Factory* d2d;
    PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, typeof(ID2D1Factory).GUID, null, (void**)&d2d).ThrowOnFailure();
    var props = new D2D1_RENDER_TARGET_PROPERTIES
    {
        type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
        pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
        dpiX = 96, dpiY = 96,
    };
    ID2D1RenderTarget* rt;
    d2d->CreateWicBitmapRenderTarget((IWICBitmap*)bmp, &props, &rt).ThrowOnFailure();

    // DirectWrite
    IDWriteFactory* dw;
    PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, typeof(IDWriteFactory).GUID, (IUnknown**)&dw).ThrowOnFailure();
    IDWriteTextFormat* fmt;
    fixed (char* fam = "Segoe UI Light") fixed (char* loc = "en-GB")
        dw->CreateTextFormat(fam, null, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_LIGHT, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, 64f, loc, &fmt).ThrowOnFailure();
    fmt->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);

    var tDraw = sw.ElapsedMilliseconds;
    rt->BeginDraw();
    rt->Clear(new D2D1_COLOR_F { r = 0.12f, g = 0.14f, b = 0.18f, a = 1f });
    ID2D1SolidColorBrush* brush;
    rt->CreateSolidColorBrush(new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 0.92f }, null, &brush).ThrowOnFailure();
    var text = DateTime.Now.ToString("HH:mm");
    fixed (char* p = text)
        rt->DrawText(p, (uint)text.Length, fmt, new D2D_RECT_F { left = W - 48 - 172, top = 48, right = W - 48, bottom = 118 }, (ID2D1Brush*)brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
    rt->EndDraw(null, null).ThrowOnFailure();
    tDraw = sw.ElapsedMilliseconds - tDraw;

    // JPEG encode q92
    var tEnc = sw.ElapsedMilliseconds;
    var outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWall", "spike.jpg");
    IWICStream* stream;
    wic->CreateStream(&stream).ThrowOnFailure();
    fixed (char* op = outPath) stream->InitializeFromFilename(op, (uint)0x80000000 /*GENERIC_WRITE*/).ThrowOnFailure();
    IWICBitmapEncoder* enc;
    wic->CreateEncoder(PInvoke.GUID_ContainerFormatJpeg, null, &enc).ThrowOnFailure();
    enc->Initialize((IStream*)stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache).ThrowOnFailure();
    IWICBitmapFrameEncode* frame; IPropertyBag2* bag;
    enc->CreateNewFrame(&frame, &bag).ThrowOnFailure();
    // ImageQuality = 0.92f
    var name = "ImageQuality";
    fixed (char* pn = name)
    {
        var pb = new PROPBAG2 { pstrName = pn };
        var v = new object(); // TODO in spike: build a VARIANT of VT_R4 0.92f; CsWin32 exposes VARIANT in Windows.Win32.System.Variant
        // bag->Write(1, &pb, &variant)
    }
    frame->Initialize(bag).ThrowOnFailure();
    frame->WriteSource((IWICBitmapSource*)bmp, null).ThrowOnFailure();
    frame->Commit().ThrowOnFailure();
    enc->Commit().ThrowOnFailure();
    tEnc = sw.ElapsedMilliseconds - tEnc;

    // Wallpaper via IDesktopWallpaper on the primary monitor
    var tApply = sw.ElapsedMilliseconds;
    IDesktopWallpaper* dwp;
    PInvoke.CoCreateInstance(typeof(DesktopWallpaper).GUID, null, CLSCTX.CLSCTX_LOCAL_SERVER, out dwp).ThrowOnFailure();
    fixed (char* op = outPath) dwp->SetWallpaper(null, op).ThrowOnFailure();
    tApply = sw.ElapsedMilliseconds - tApply;

    var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
    Console.WriteLine($"draw={tDraw} ms enc={tEnc} ms apply={tApply} ms total={sw.ElapsedMilliseconds} ms cpu={cpu:N0} ms ws={proc.WorkingSet64 / 1024 / 1024} MB");
    Console.WriteLine("loaded gpu driver dlls: " + string.Join(",", proc.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).Where(n => n.StartsWith("nv", StringComparison.OrdinalIgnoreCase) || n.StartsWith("amd", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ig", StringComparison.OrdinalIgnoreCase) || n.Contains("d3d", StringComparison.OrdinalIgnoreCase))));
}
```

The one marked `TODO in spike` is the only unknown worth the spike's time: how CsWin32
exposes `VARIANT` for `IPropertyBag2.Write`. Resolve it (expected: `Windows.Win32.System.Variant.VARIANT`
with `Anonymous.Anonymous.vt = VARENUM.VT_R4` and `.Anonymous.Anonymous.Anonymous.fltVal = 0.92f`)
and record the final code.

- [ ] **Step 3: Run three times, published AOT**

```bash
dotnet publish spikes/D2dWic -c Release -r win-x64
.\spikes\D2dWic\bin\Release\net10.0-windows\win-x64\publish\D2dWic.exe
```

Expected: the wallpaper changes to a dark frame with the time at top right; the last line
lists **no** GPU driver DLLs; encode of a 3440x1440 frame in the tens of milliseconds.

- [ ] **Step 4: Write the results file**

`docs/superpowers/plans/2026-09-20-phase1-spike-results.md` with: the three timing lines,
the working set, the DLL list, every CsWin32 name that differed from Step 1, the final
`VARIANT` code, and the exact signatures used for every call in Step 2. Task 8 is written
against this file.

Decision recorded in the file: if `enc` is over 40 ms, Task 11 (incremental redraw) becomes
mandatory before Task 10's budget check; otherwise Task 11 stays but is measured, not assumed.

- [ ] **Step 5: Remove the spike and commit**

```bash
git rm -r spikes
git add docs/superpowers/plans/2026-09-20-phase1-spike-results.md
git commit -m "Phase 1 spike: CsWin32 software Direct2D + WIC JPEG + IDesktopWallpaper timings"
```

---

### Task 2: The seam: values, `ISource`, resolved components, `Color`, `TextStyle`

Everything the three lanes build against. Frozen once merged; changes go through the
integrator.

**Files:**
- Create: `src/DeskWall.Core/Values/Value.cs`, `src/DeskWall.Core/Values/ValueTree.cs`,
  `src/DeskWall.Core/Sources/ISource.cs`, `src/DeskWall.Core/Sources/SourceSnapshot.cs`,
  `src/DeskWall.Core/Resolve/Resolved.cs`, `src/DeskWall.Core/Render/Color.cs`,
  `src/DeskWall.Core/Render/TextStyle.cs`, `src/DeskWall.Core/Geometry.cs`
- Test: `tests/DeskWall.Core.Tests/Values/ValueTests.cs`, `tests/DeskWall.Core.Tests/Render/ColorTests.cs`

**Interfaces:**
- Produces everything below, verbatim.

- [ ] **Step 1: Failing tests**

`tests/DeskWall.Core.Tests/Values/ValueTests.cs`:

```csharp
using DeskWall.Core.Values;
using Xunit;

public class ValueTests
{
    [Fact]
    public void ListValue_LookupByKey_FindsRecord()
    {
        var c = new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("C"), ["free"] = new NumberValue(120e9) });
        var d = new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("D"), ["free"] = new NumberValue(9e9) });
        var list = new ListValue([c, d], KeyField: "letter");
        Assert.Same(d, list.ByKey("D"));
        Assert.Null(list.ByKey("E"));
    }

    [Fact]
    public void Value_ToText_UsesInvariantCulture()
    {
        Assert.Equal("1234.5", new NumberValue(1234.5).ToText(null));
        Assert.Equal("1,235", new NumberValue(1234.5).ToText("N0"));
        Assert.Equal("09:05", new TimeValue(new DateTimeOffset(2026, 9, 20, 9, 5, 0, TimeSpan.Zero)).ToText("HH:mm"));
        Assert.Equal("yes", new BoolValue(true).ToText(null) == "True" ? "yes" : "no");
        Assert.Equal("https://x/10/cover.jpg", new NumberValue(10).ToText("https://x/{0}/cover.jpg"));
    }
}
```

`tests/DeskWall.Core.Tests/Render/ColorTests.cs`:

```csharp
using DeskWall.Core.Render;
using Xunit;

public class ColorTests
{
    [Theory]
    [InlineData("#FFFFFF", 255, 255, 255, 255)]
    [InlineData("#80FF0000", 128, 255, 0, 0)]
    [InlineData("#abc", 255, 170, 187, 204)]
    public void Parse_Hex(string s, byte a, byte r, byte g, byte b)
    {
        var c = Color.Parse(s);
        Assert.Equal((a, r, g, b), (c.A, c.R, c.G, c.B));
    }

    [Fact]
    public void Parse_Invalid_Throws() => Assert.Throws<FormatException>(() => Color.Parse("red"));
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter "FullyQualifiedName~ValueTests|FullyQualifiedName~ColorTests"`
Expected: build errors, types missing.

- [ ] **Step 3: Implement the seam**

`src/DeskWall.Core/Geometry.cs`:

```csharp
namespace DeskWall.Core;

/// <summary>Physical pixels. Immutable.</summary>
public readonly record struct Rect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Intersects(Rect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public Rect Offset(int dx, int dy) => new(X + dx, Y + dy, W, H);
    public Rect Scale(double sx, double sy) => new((int)Math.Round(X * sx), (int)Math.Round(Y * sy), (int)Math.Round(W * sx), (int)Math.Round(H * sy));
}

public enum Fit { Cover, Contain, Stretch }
public enum Axis { Vertical, Horizontal }
public enum Align { Left, Center, Right }
```

`src/DeskWall.Core/Values/Value.cs`:

```csharp
using System.Globalization;

namespace DeskWall.Core.Values;

/// <summary>A value published by a source. Closed hierarchy; pattern-match on it.</summary>
public abstract record Value
{
    /// <summary>Render as text. <paramref name="format"/> is a .NET format string for the
    /// value's type, or a composite format containing {0}, or null for the default.</summary>
    public string ToText(string? format)
    {
        var inv = CultureInfo.InvariantCulture;
        if (format is not null && format.Contains("{0"))
            return string.Format(inv, format, Raw());
        return this switch
        {
            TextValue t => t.Text,
            NumberValue n => n.Number.ToString(format, inv),
            TimeValue t => t.Time.ToString(format ?? "o", inv),
            BoolValue b => b.Flag ? "True" : "False",
            ImageValue i => i.Path,
            ListValue l => $"[{l.Items.Count} items]",
            RecordValue r => $"{{{r.Fields.Count} fields}}",
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>The CLR object for composite formatting.</summary>
    public object Raw() => this switch
    {
        TextValue t => t.Text,
        NumberValue n => n.Number,
        TimeValue t => t.Time,
        BoolValue b => b.Flag,
        ImageValue i => i.Path,
        _ => ToText(null),
    };
}

public sealed record TextValue(string Text) : Value;
public sealed record NumberValue(double Number) : Value;
public sealed record TimeValue(DateTimeOffset Time) : Value;
public sealed record BoolValue(bool Flag) : Value;
/// <summary>Local path or http(s) URL. The render path only ever opens local paths; the
/// image cache (Phase 4) rewrites URLs to cache paths before rendering.</summary>
public sealed record ImageValue(string Path) : Value;

public sealed record RecordValue(IReadOnlyDictionary<string, Value> Fields) : Value
{
    public Value? Get(string name) => Fields.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Ordered records. <paramref name="KeyField"/> names the field used by [key] lookups.</summary>
public sealed record ListValue(IReadOnlyList<RecordValue> Items, string? KeyField) : Value
{
    public RecordValue? ByKey(string key)
    {
        if (KeyField is null) return null;
        foreach (var r in Items)
            if (r.Get(KeyField) is TextValue t && string.Equals(t.Text, key, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }
}
```

`src/DeskWall.Core/Values/ValueTree.cs`:

```csharp
namespace DeskWall.Core.Values;

/// <summary>Root of all published values: one field per source name.</summary>
public static class ValueTree
{
    public static RecordValue Empty { get; } = new(new Dictionary<string, Value>());

    public static RecordValue Of(params (string Name, Value Value)[] fields)
        => new(fields.ToDictionary(f => f.Name, f => f.Value));
}
```

`src/DeskWall.Core/Sources/ISource.cs`:

```csharp
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>A provider that runs on its own schedule and publishes a RecordValue.
/// Implementations must be safe to call from a background thread and must not hold
/// resources between refreshes.</summary>
public interface ISource
{
    /// <summary>Instance name used as the root field in the value tree (from the layout).</summary>
    string Name { get; }

    /// <summary>Next time this source is due, given the last refresh (null = never ran).</summary>
    DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now);

    /// <summary>Produce the current values. Throwing marks the source failed (spec 3.2).</summary>
    ValueTask<RecordValue> RefreshAsync(CancellationToken ct);
}

/// <summary>Helper for the common "every N" schedule.</summary>
public abstract class PeriodicSource(string name, TimeSpan every) : ISource
{
    public string Name => name;
    public TimeSpan Every => every;
    public virtual DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
        => lastRefresh is null ? now : lastRefresh.Value + every;
    public abstract ValueTask<RecordValue> RefreshAsync(CancellationToken ct);
}
```

`src/DeskWall.Core/Sources/SourceSnapshot.cs`:

```csharp
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Last known state of one source. Immutable; the registry swaps whole snapshots.</summary>
public sealed record SourceSnapshot(
    string Name,
    RecordValue? Values,
    DateTimeOffset? LastRefresh,
    string? LastError,
    int ConsecutiveFailures)
{
    public static SourceSnapshot Initial(string name) => new(name, null, null, null, 0);
    public SourceSnapshot Succeeded(RecordValue v, DateTimeOffset at) => this with { Values = v, LastRefresh = at, LastError = null, ConsecutiveFailures = 0 };
    public SourceSnapshot Failed(string error) => this with { LastError = error, ConsecutiveFailures = ConsecutiveFailures + 1 };
}

/// <summary>All snapshots, keyed by source name. Builds the value tree for resolution.</summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, SourceSnapshot> _snaps = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<SourceSnapshot> All => _snaps.Values;
    public SourceSnapshot Get(string name) => _snaps.TryGetValue(name, out var s) ? s : SourceSnapshot.Initial(name);
    public void Set(SourceSnapshot s) => _snaps[s.Name] = s;

    public RecordValue Tree()
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _snaps.Values) if (s.Values is not null) d[s.Name] = s.Values;
        return new RecordValue(d);
    }
}
```

`src/DeskWall.Core/Render/Color.cs`:

```csharp
using System.Globalization;

namespace DeskWall.Core.Render;

public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    public static Color White => new(255, 255, 255, 255);
    public static Color Transparent => new(0, 0, 0, 0);

    /// <summary>#RGB, #RRGGBB or #AARRGGBB.</summary>
    public static Color Parse(string s)
    {
        if (s.Length is not (4 or 7 or 9) || s[0] != '#') throw new FormatException($"bad colour '{s}'");
        if (s.Length == 4) s = $"#{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}";
        var v = uint.Parse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (s.Length == 7) v |= 0xFF000000;
        return new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
```

`src/DeskWall.Core/Render/TextStyle.cs`:

```csharp
namespace DeskWall.Core.Render;

public enum TextEffect { None, Shadow, Outline, Plate }

/// <summary>Resolved text appearance. Defaults match the spec: soft shadow, 6 px blur.</summary>
public sealed record TextStyle(
    string Font = "Segoe UI",
    float Size = 16f,
    int Weight = 400,
    Color Color = default,
    Align Align = Align.Left,
    TextEffect Effect = TextEffect.Shadow,
    float EffectRadius = 6f,
    Color EffectColor = default)
{
    public static TextStyle Default => new(Color: Color.White, EffectColor: new Color(160, 0, 0, 0));
}
```

`src/DeskWall.Core/Resolve/Resolved.cs`:

```csharp
using DeskWall.Core.Render;

namespace DeskWall.Core.Resolve;

/// <summary>A component after bindings are resolved and repeaters expanded. Renderers and
/// the shortcut manager consume only these. ContentKey is set by the resolver (Task 6).</summary>
public abstract record Resolved(string Id, Rect Rect, int Z)
{
    public string ContentKey { get; init; } = "";
    /// <summary>The fields that define appearance, in a fixed order, for hashing.</summary>
    public abstract IEnumerable<string> KeyParts();
}

public sealed record ResolvedText(string Id, Rect Rect, int Z, string Text, TextStyle Style) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Text, Style.ToString()];
}

public sealed record ResolvedImage(string Id, Rect Rect, int Z, string Path, Fit Fit, float Radius, float Opacity) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Path, Fit.ToString(), Radius.ToString("R"), Opacity.ToString("R")];
}

public sealed record ResolvedBar(string Id, Rect Rect, int Z, double Fraction, Color Track, Color Fill, Axis Direction) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Fraction.ToString("R"), Track.ToHex(), Fill.ToHex(), Direction.ToString()];
}

/// <summary>Draws nothing. The shortcut manager (Phase 3) owns a desktop icon over Rect.</summary>
public sealed record ResolvedShortcut(string Id, Rect Rect, int Z, string Target, string Tooltip, int Slot) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Target, Tooltip, Slot.ToString()];
}
```

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test --filter "FullyQualifiedName~ValueTests|FullyQualifiedName~ColorTests"`
Expected: 5 passing.

- [ ] **Step 5: Commit and announce the freeze**

```bash
git add src/DeskWall.Core tests/DeskWall.Core.Tests
git commit -m "Core seam: Value tree, ISource, SourceRegistry, Resolved records, Color, TextStyle"
```

Fan-out starts here. Create the three lane worktrees from `v1`.

---

### Task 3: Binding parser and resolver (lane `v1/p1-binding`)

**Files:**
- Create: `src/DeskWall.Core/Binding/Binding.cs`, `src/DeskWall.Core/Binding/BindingParser.cs`,
  `src/DeskWall.Core/Binding/BindingResolver.cs`
- Test: `tests/DeskWall.Core.Tests/Binding/BindingParserTests.cs`, `tests/DeskWall.Core.Tests/Binding/BindingResolverTests.cs`

**Interfaces:**
- Consumes: `Value` hierarchy, `RecordValue.Get`, `ListValue.ByKey`, `Value.ToText` (Task 2).
- Produces:
  - `Binding(IReadOnlyList<PathSegment> Path, string? Format)` with `static Binding Parse(string)` and `string ToString()` round-tripping.
  - `PathSegment` = `NameSegment(string Name)` | `IndexSegment(int Index)` | `KeySegment(string Key)`.
  - `BindingResolver.Resolve(Binding, RecordValue root) : Value?` (null when any step is missing).
  - `BindingResolver.ResolveText(Binding, RecordValue root) : string?`.

Grammar (spec 4.2): `path [ '|' format ]`. Path is `name ( '.' name | '[' int ']' | '[' key ']' )*`.
Format is either bare (`HH:mm`) or double-quoted (`"{0:N0} GB free"`); quotes are stripped.
Names are `[A-Za-z_][A-Za-z0-9_-]*`. A bracket body that parses as an int is an index,
otherwise a key.

- [ ] **Step 1: Failing parser tests**

```csharp
using DeskWall.Core.Binding;
using Xunit;

public class BindingParserTests
{
    [Fact]
    public void Parses_Path_And_QuotedFormat()
    {
        var b = Binding.Parse("disks.drives[C].free | \"{0:N0} GB free\"");
        Assert.Collection(b.Path,
            s => Assert.Equal(new NameSegment("disks"), s),
            s => Assert.Equal(new NameSegment("drives"), s),
            s => Assert.Equal(new KeySegment("C"), s),
            s => Assert.Equal(new NameSegment("free"), s));
        Assert.Equal("{0:N0} GB free", b.Format);
    }

    [Fact]
    public void Parses_Index_And_BareFormat()
    {
        var b = Binding.Parse("time.now|HH:mm");
        Assert.Equal("HH:mm", b.Format);
        var c = Binding.Parse("steam.json.games[0].appid");
        Assert.Equal(new IndexSegment(0), c.Path[2]);
        Assert.Null(c.Format);
    }

    [Theory]
    [InlineData("")]
    [InlineData("time.")]
    [InlineData("time[")]
    [InlineData("time..now")]
    [InlineData("9lives")]
    public void Rejects_Malformed(string text) => Assert.Throws<FormatException>(() => Binding.Parse(text));

    [Fact]
    public void ToString_RoundTrips()
    {
        const string s = "disks.drives[C].free | \"{0:N0} GB free\"";
        Assert.Equal(s, Binding.Parse(s).ToString());
    }
}
```

- [ ] **Step 2: Failing resolver tests**

```csharp
using DeskWall.Core.Binding;
using DeskWall.Core.Values;
using Xunit;

public class BindingResolverTests
{
    private static RecordValue Root()
    {
        var c = new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("C"), ["free"] = new NumberValue(120_000_000_000) });
        var disks = new RecordValue(new Dictionary<string, Value> { ["drives"] = new ListValue([c], "letter") });
        var time = new RecordValue(new Dictionary<string, Value> { ["now"] = new TimeValue(new DateTimeOffset(2026, 9, 20, 14, 32, 0, TimeSpan.Zero)) });
        return ValueTree.Of(("disks", disks), ("time", time));
    }

    [Fact]
    public void Resolves_Key_Lookup_And_Formats()
        => Assert.Equal("120,000,000,000 GB free", BindingResolver.ResolveText(Binding.Parse("disks.drives[C].free | \"{0:N0} GB free\""), Root()));

    [Fact]
    public void Resolves_Time_Format()
        => Assert.Equal("14:32", BindingResolver.ResolveText(Binding.Parse("time.now | HH:mm"), Root()));

    [Fact]
    public void Resolves_Index()
        => Assert.Equal("C", BindingResolver.ResolveText(Binding.Parse("disks.drives[0].letter"), Root()));

    [Theory]
    [InlineData("disks.drives[Z].free")]
    [InlineData("disks.drives[5].free")]
    [InlineData("nope.now")]
    [InlineData("time.now.hour")]
    public void Missing_Returns_Null(string s) => Assert.Null(BindingResolver.Resolve(Binding.Parse(s), Root()));
}
```

- [ ] **Step 3: Run, expect compile failure**

Run: `dotnet test --filter FullyQualifiedName~Binding`

- [ ] **Step 4: Implement**

`src/DeskWall.Core/Binding/Binding.cs`:

```csharp
namespace DeskWall.Core.Binding;

public abstract record PathSegment;
public sealed record NameSegment(string Name) : PathSegment { public override string ToString() => Name; }
public sealed record IndexSegment(int Index) : PathSegment { public override string ToString() => $"[{Index}]"; }
public sealed record KeySegment(string Key) : PathSegment { public override string ToString() => $"[{Key}]"; }

/// <summary>A path into the value tree plus an optional format. See spec 4.2.</summary>
public sealed record Binding(IReadOnlyList<PathSegment> Path, string? Format)
{
    public static Binding Parse(string text) => BindingParser.Parse(text);

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Path.Count; i++)
        {
            if (i > 0 && Path[i] is NameSegment) sb.Append('.');
            sb.Append(Path[i]);
        }
        if (Format is not null) sb.Append(" | \"").Append(Format).Append('"');
        return sb.ToString();
    }
}
```

`src/DeskWall.Core/Binding/BindingParser.cs`:

```csharp
namespace DeskWall.Core.Binding;

public static class BindingParser
{
    public static Binding Parse(string text)
    {
        var bar = text.IndexOf('|');
        var pathText = (bar < 0 ? text : text[..bar]).Trim();
        string? format = null;
        if (bar >= 0)
        {
            format = text[(bar + 1)..].Trim();
            if (format.Length >= 2 && format[0] == '"' && format[^1] == '"') format = format[1..^1];
            if (format.Length == 0) throw new FormatException("empty format");
        }
        return new Binding(ParsePath(pathText), format);
    }

    private static List<PathSegment> ParsePath(string s)
    {
        if (s.Length == 0) throw new FormatException("empty path");
        var segs = new List<PathSegment>();
        var i = 0;
        segs.Add(new NameSegment(ReadName(s, ref i)));
        while (i < s.Length)
        {
            if (s[i] == '.')
            {
                i++;
                segs.Add(new NameSegment(ReadName(s, ref i)));
            }
            else if (s[i] == '[')
            {
                var close = s.IndexOf(']', i);
                if (close < 0) throw new FormatException("unclosed [");
                var body = s[(i + 1)..close];
                if (body.Length == 0) throw new FormatException("empty []");
                segs.Add(int.TryParse(body, out var n) && n >= 0 ? new IndexSegment(n) : new KeySegment(body));
                i = close + 1;
            }
            else throw new FormatException($"unexpected '{s[i]}' at {i}");
        }
        return segs;
    }

    private static string ReadName(string s, ref int i)
    {
        var start = i;
        if (i >= s.Length || !(char.IsAsciiLetter(s[i]) || s[i] == '_')) throw new FormatException($"expected name at {i}");
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '-')) i++;
        return s[start..i];
    }
}
```

`src/DeskWall.Core/Binding/BindingResolver.cs`:

```csharp
using DeskWall.Core.Values;

namespace DeskWall.Core.Binding;

public static class BindingResolver
{
    /// <summary>Walk the path. Null if any step is missing or of the wrong shape.</summary>
    public static Value? Resolve(Binding b, RecordValue root)
    {
        Value? cur = root;
        foreach (var seg in b.Path)
        {
            cur = (seg, cur) switch
            {
                (NameSegment n, RecordValue r) => r.Get(n.Name),
                (IndexSegment ix, ListValue l) => ix.Index < l.Items.Count ? l.Items[ix.Index] : null,
                (KeySegment k, ListValue l) => l.ByKey(k.Key),
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    public static string? ResolveText(Binding b, RecordValue root) => Resolve(b, root)?.ToText(b.Format);
}
```

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test --filter FullyQualifiedName~Binding`
Expected: 12 passing.

- [ ] **Step 6: Commit**

```bash
git add src/DeskWall.Core/Binding tests/DeskWall.Core.Tests/Binding
git commit -m "Binding: path+format parser and resolver over the value tree"
```

---

### Task 4: Layout JSON model (lane `v1/p1-binding`)

**Files:**
- Create: `src/DeskWall.Core/Layout/PropertyValue.cs`, `src/DeskWall.Core/Layout/ComponentDef.cs`,
  `src/DeskWall.Core/Layout/SourceDef.cs`, `src/DeskWall.Core/Layout/LayoutFile.cs`
- Test: `tests/DeskWall.Core.Tests/Layout/LayoutFileTests.cs`

**Interfaces:**
- Consumes: `Binding.Parse` (Task 3), `Rect`, `Fit`, `Axis`, `Align` (Task 2).
- Produces:
  - `PropertyValue` with `static PropertyValue Literal(string)`, `static PropertyValue Bound(Binding)`, `Binding? Binding`, `string? LiteralText`.
  - `ComponentDef(string Id, Rect Rect, int Z)` abstract, with subclasses `TextDef`, `ImageDef`, `BarDef`, `ShortcutDef`, `RepeaterDef`, fields listed below.
  - `SourceDef(string Name, string Type, int? EverySeconds, Dictionary<string,string> Settings)`.
  - `LayoutFile(int Version, string BaseImage, Fit BaseFit, string Encode, int JpegQuality, List<SourceDef> Sources, List<ComponentDef> Components)`.
  - `LayoutFile.Load(string path)`, `LayoutFile.Parse(string json)`, `layout.Save(string path)`.
  - `LayoutJsonContext` (source-generated).

JSON shape:

```json
{
  "version": 1,
  "baseImage": "C:\\Users\\Joe\\Pictures\\wall.jpg",
  "baseFit": "cover",
  "encode": "jpeg",
  "jpegQuality": 92,
  "sources": [
    { "name": "time", "type": "time" },
    { "name": "disks", "type": "disks", "every": 300 }
  ],
  "components": [
    { "type": "text", "id": "clock", "rect": [3220, 48, 172, 70], "z": 1,
      "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "align": "right" },
    { "type": "repeater", "id": "drives", "rect": [3220, 1260, 172, 92], "z": 1,
      "items": { "bind": "disks.drives" }, "axis": "vertical", "gap": 0, "cellHeight": 46,
      "template": [
        { "type": "text", "id": "letter", "rect": [0, 0, 60, 24], "text": { "bind": "letter | \"{0}:\"" }, "size": 15 },
        { "type": "text", "id": "free", "rect": [60, 0, 112, 24], "text": { "bind": "freeGB | \"{0:N0} GB free\"" }, "size": 15, "align": "right" },
        { "type": "bar", "id": "bar", "rect": [0, 26, 172, 6], "fraction": { "bind": "usedFraction" }, "threshold": 0.85, "thresholdFill": "#D13438" }
      ] }
  ]
}
```

A property is a JSON string (literal) or `{ "bind": "..." }`. Numbers may be given as JSON
numbers for literal numeric properties. Inside a repeater template, binding paths are
relative to the current item record.

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core;
using DeskWall.Core.Layout;
using Xunit;

public class LayoutFileTests
{
    private const string Json = """
    {
      "version": 1, "baseImage": "C:\\wall.jpg", "baseFit": "cover", "encode": "jpeg", "jpegQuality": 92,
      "sources": [ { "name": "time", "type": "time" }, { "name": "disks", "type": "disks", "every": 300 } ],
      "components": [
        { "type": "text", "id": "clock", "rect": [3220, 48, 172, 70], "z": 1, "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "align": "right" },
        { "type": "repeater", "id": "drives", "rect": [3220, 1260, 172, 92], "items": { "bind": "disks.drives" }, "axis": "vertical", "gap": 0, "cellHeight": 46,
          "template": [
            { "type": "bar", "id": "bar", "rect": [0, 26, 172, 6], "fraction": { "bind": "usedFraction" }, "threshold": 0.85, "thresholdFill": "#D13438" },
            { "type": "shortcut", "id": "go", "rect": [0, 0, 172, 46], "target": { "bind": "letter | \"explorer.exe {0}:\\\\\"" }, "tooltip": "Open" }
          ] }
      ]
    }
    """;

    [Fact]
    public void Parses_Header_And_Sources()
    {
        var l = LayoutFile.Parse(Json);
        Assert.Equal(1, l.Version);
        Assert.Equal(Fit.Cover, l.BaseFit);
        Assert.Equal(92, l.JpegQuality);
        Assert.Equal(300, l.Sources[1].EverySeconds);
        Assert.Null(l.Sources[0].EverySeconds);
    }

    [Fact]
    public void Parses_Polymorphic_Components()
    {
        var l = LayoutFile.Parse(Json);
        var clock = Assert.IsType<TextDef>(l.Components[0]);
        Assert.Equal(new Rect(3220, 48, 172, 70), clock.Rect);
        Assert.Equal("time.now | \"HH:mm\"", clock.Text.Binding!.ToString());
        Assert.Equal("Segoe UI Light", clock.Font.LiteralText);
        Assert.Equal("64", clock.Size.LiteralText);
        var rep = Assert.IsType<RepeaterDef>(l.Components[1]);
        Assert.Equal(Axis.Vertical, rep.Axis);
        Assert.Equal("46", rep.CellHeight.LiteralText);
        Assert.IsType<BarDef>(rep.Template[0]);
        var sc = Assert.IsType<ShortcutDef>(rep.Template[1]);
        Assert.Equal("Open", sc.Tooltip.LiteralText);
    }

    [Fact]
    public void Defaults_Apply()
    {
        var l = LayoutFile.Parse("""{ "version": 1, "baseImage": "x.jpg", "sources": [], "components": [ { "type": "text", "id": "t", "rect": [0,0,10,10], "text": "hi" } ] }""");
        Assert.Equal(Fit.Cover, l.BaseFit);
        Assert.Equal("jpeg", l.Encode);
        Assert.Equal(92, l.JpegQuality);
        var t = (TextDef)l.Components[0];
        Assert.Equal(0, t.Z);
        Assert.Equal("hi", t.Text.LiteralText);
        Assert.Equal("Segoe UI", t.Font.LiteralText);
    }

    [Fact]
    public void RoundTrips()
    {
        var l = LayoutFile.Parse(Json);
        var again = LayoutFile.Parse(l.ToJson());
        Assert.Equal(l.ToJson(), again.ToJson());
    }

    [Fact]
    public void Unknown_Type_Throws()
        => Assert.ThrowsAny<Exception>(() => LayoutFile.Parse("""{ "version": 1, "baseImage": "x", "sources": [], "components": [ { "type": "gauge", "id": "g", "rect": [0,0,1,1] } ] }"""));
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter FullyQualifiedName~LayoutFile`

- [ ] **Step 3: Implement**

`src/DeskWall.Core/Layout/PropertyValue.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Binding;

namespace DeskWall.Core.Layout;

/// <summary>A component property: a literal string or a binding. Numbers are literals in
/// invariant text; the resolver parses them.</summary>
[JsonConverter(typeof(PropertyValueConverter))]
public sealed class PropertyValue
{
    public string? LiteralText { get; }
    public Binding.Binding? Binding { get; }

    private PropertyValue(string? literal, Binding.Binding? binding) { LiteralText = literal; Binding = binding; }

    public static PropertyValue Literal(string s) => new(s, null);
    public static PropertyValue Literal(double d) => new(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture), null);
    public static PropertyValue Bound(Binding.Binding b) => new(null, b);
    public bool IsBound => Binding is not null;
    public override string ToString() => IsBound ? $"{{bind {Binding}}}" : LiteralText ?? "";
}

public sealed class PropertyValueConverter : JsonConverter<PropertyValue>
{
    public override PropertyValue Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.String: return PropertyValue.Literal(r.GetString()!);
            case JsonTokenType.Number: return PropertyValue.Literal(r.GetDouble());
            case JsonTokenType.True: return PropertyValue.Literal("true");
            case JsonTokenType.False: return PropertyValue.Literal("false");
            case JsonTokenType.StartObject:
                string? bind = null;
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    var name = r.GetString(); r.Read();
                    if (name == "bind") bind = r.GetString();
                    else r.Skip();
                }
                if (bind is null) throw new JsonException("property object needs \"bind\"");
                return PropertyValue.Bound(Binding.Binding.Parse(bind));
            default: throw new JsonException($"bad property token {r.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter w, PropertyValue v, JsonSerializerOptions o)
    {
        if (v.IsBound) { w.WriteStartObject(); w.WriteString("bind", v.Binding!.ToString()); w.WriteEndObject(); }
        else w.WriteStringValue(v.LiteralText);
    }
}

/// <summary>Rect as [x, y, w, h].</summary>
public sealed class RectConverter : JsonConverter<Rect>
{
    public override Rect Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("rect must be [x,y,w,h]");
        var v = new int[4]; var i = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) { if (i > 3) throw new JsonException("rect has 4 numbers"); v[i++] = r.GetInt32(); }
        if (i != 4) throw new JsonException("rect has 4 numbers");
        return new Rect(v[0], v[1], v[2], v[3]);
    }
    public override void Write(Utf8JsonWriter w, Rect v, JsonSerializerOptions o)
    { w.WriteStartArray(); w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteNumberValue(v.W); w.WriteNumberValue(v.H); w.WriteEndArray(); }
}
```

`src/DeskWall.Core/Layout/ComponentDef.cs`:

```csharp
using System.Text.Json.Serialization;

namespace DeskWall.Core.Layout;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextDef), "text")]
[JsonDerivedType(typeof(ImageDef), "image")]
[JsonDerivedType(typeof(BarDef), "bar")]
[JsonDerivedType(typeof(ShortcutDef), "shortcut")]
[JsonDerivedType(typeof(RepeaterDef), "repeater")]
public abstract class ComponentDef
{
    public required string Id { get; set; }
    [JsonConverter(typeof(RectConverter))] public required Rect Rect { get; set; }
    public int Z { get; set; }
}

public sealed class TextDef : ComponentDef
{
    public required PropertyValue Text { get; set; }
    public PropertyValue Font { get; set; } = PropertyValue.Literal("Segoe UI");
    public PropertyValue Size { get; set; } = PropertyValue.Literal(16);
    public PropertyValue Weight { get; set; } = PropertyValue.Literal(400);
    public PropertyValue Color { get; set; } = PropertyValue.Literal("#EBFFFFFF");
    public PropertyValue Align { get; set; } = PropertyValue.Literal("left");
    public PropertyValue Effect { get; set; } = PropertyValue.Literal("shadow");
    public PropertyValue EffectRadius { get; set; } = PropertyValue.Literal(6);
    public PropertyValue EffectColor { get; set; } = PropertyValue.Literal("#A0000000");
}

public sealed class ImageDef : ComponentDef
{
    public required PropertyValue Source { get; set; }
    public PropertyValue Fit { get; set; } = PropertyValue.Literal("cover");
    public PropertyValue Radius { get; set; } = PropertyValue.Literal(0);
    public PropertyValue Opacity { get; set; } = PropertyValue.Literal(1);
}

public sealed class BarDef : ComponentDef
{
    public required PropertyValue Fraction { get; set; }
    public PropertyValue Track { get; set; } = PropertyValue.Literal("#46FFFFFF");
    public PropertyValue Fill { get; set; } = PropertyValue.Literal("#EBFFFFFF");
    public PropertyValue Threshold { get; set; } = PropertyValue.Literal(1);
    public PropertyValue ThresholdFill { get; set; } = PropertyValue.Literal("#D13438");
    public PropertyValue Direction { get; set; } = PropertyValue.Literal("horizontal");
}

public sealed class ShortcutDef : ComponentDef
{
    public required PropertyValue Target { get; set; }
    public PropertyValue Tooltip { get; set; } = PropertyValue.Literal("");
    /// <summary>Base slot; repeater children add their index.</summary>
    public int Slot { get; set; }
}

public sealed class RepeaterDef : ComponentDef
{
    public required PropertyValue Items { get; set; }
    public Axis Axis { get; set; } = Axis.Vertical;
    public int Gap { get; set; }
    /// <summary>Pixels, or "auto" to take the height from the first image child's aspect ratio.</summary>
    public PropertyValue CellHeight { get; set; } = PropertyValue.Literal("auto");
    public required List<ComponentDef> Template { get; set; }
}
```

`src/DeskWall.Core/Layout/SourceDef.cs`:

```csharp
using System.Text.Json.Serialization;

namespace DeskWall.Core.Layout;

public sealed class SourceDef
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    [JsonPropertyName("every")] public int? EverySeconds { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();
}
```

`src/DeskWall.Core/Layout/LayoutFile.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Layout;

public sealed class LayoutFile
{
    public int Version { get; set; } = 1;
    public required string BaseImage { get; set; }
    public Fit BaseFit { get; set; } = Fit.Cover;
    /// <summary>"jpeg" or "png".</summary>
    public string Encode { get; set; } = "jpeg";
    public int JpegQuality { get; set; } = 92;
    public List<SourceDef> Sources { get; set; } = new();
    public List<ComponentDef> Components { get; set; } = new();

    public static LayoutFile Parse(string json)
        => JsonSerializer.Deserialize(json, LayoutJsonContext.Default.LayoutFile) ?? throw new JsonException("empty layout");

    public static LayoutFile Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, LayoutJsonContext.Default.LayoutFile);

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LayoutFile))]
public partial class LayoutJsonContext : JsonSerializerContext;
```

Enum values serialise camel-case (`cover`, `vertical`) because of `UseStringEnumConverter`
with the camel-case policy; confirm in the round-trip test output.

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test --filter FullyQualifiedName~LayoutFile`
Expected: 5 passing. Also `dotnet publish src/DeskWall.Daemon -c Release -r win-x64` still has
zero IL warnings (the source-generated context is what keeps it that way).

- [ ] **Step 5: Commit**

```bash
git add src/DeskWall.Core/Layout tests/DeskWall.Core.Tests/Layout
git commit -m "Layout: polymorphic component defs, PropertyValue literal-or-bind, source-generated JSON"
```

Lane `v1/p1-binding` is complete: rebase on `v1`, `dotnet test`, fast-forward merge.

---

### Task 5: `time` and `disks` sources plus the factory (lane `v1/p1-sources`)

**Files:**
- Create: `src/DeskWall.Core/Sources/TimeSource.cs`, `src/DeskWall.Core/Sources/DisksSource.cs`,
  `src/DeskWall.Core/Sources/SourceFactory.cs`, `src/DeskWall.Core/Sources/IClock.cs`
- Test: `tests/DeskWall.Core.Tests/Sources/TimeSourceTests.cs`, `tests/DeskWall.Core.Tests/Sources/DisksSourceTests.cs`, `tests/DeskWall.Core.Tests/Sources/SourceFactoryTests.cs`

**Interfaces:**
- Consumes: `ISource`, `PeriodicSource`, `RecordValue`, `ListValue`, `SourceDef` (Tasks 2, 4).
- Produces:
  - `IClock { DateTimeOffset Now { get; } }`, `SystemClock.Instance`.
  - `TimeSource(string name, IClock clock)`: publishes `now` (TimeValue, local), `date` (TextValue yyyy-MM-dd), `weekday` (TextValue). `NextDue` = next whole minute after `lastRefresh`, or `now` if never.
  - `DisksSource(string name, TimeSpan every, Func<IReadOnlyList<DriveInfo>>? drives = null)`: publishes `drives` (ListValue keyed by `letter`) with fields `letter`, `label`, `free`, `total`, `freeGB`, `totalGB`, `usedFraction`. Fixed drives only, ready ones only.
  - `SourceFactory.Create(SourceDef def, IClock clock) : ISource`; throws `NotSupportedException` for unknown types. Phase 4 adds cases here.

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

public class TimeSourceTests
{
    [Fact]
    public async Task Publishes_Now_Date_Weekday()
    {
        var t = new DateTimeOffset(2026, 9, 20, 14, 32, 17, TimeSpan.FromHours(1));
        var src = new TimeSource("time", new FakeClock(t));
        var v = await src.RefreshAsync(default);
        Assert.Equal(t, ((TimeValue)v.Get("now")!).Time);
        Assert.Equal("2026-09-20", ((TextValue)v.Get("date")!).Text);
        Assert.Equal("Sunday", ((TextValue)v.Get("weekday")!).Text);
    }

    [Fact]
    public void NextDue_IsNextWholeMinute()
    {
        var t = new DateTimeOffset(2026, 9, 20, 14, 32, 17, TimeSpan.Zero);
        var src = new TimeSource("time", new FakeClock(t));
        Assert.Equal(t, src.NextDue(null, t));
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 14, 33, 0, TimeSpan.Zero), src.NextDue(t, t));
    }
}

public class DisksSourceTests
{
    [Fact]
    public async Task Publishes_Fixed_Ready_Drives_Keyed_By_Letter()
    {
        var real = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
        var src = new DisksSource("disks", TimeSpan.FromMinutes(5), () => real);
        var v = await src.RefreshAsync(default);
        var list = (ListValue)v.Get("drives")!;
        Assert.Equal("letter", list.KeyField);
        Assert.Equal(real.Count, list.Items.Count);
        var c = list.ByKey("C")!;
        var frac = ((NumberValue)c.Get("usedFraction")!).Number;
        Assert.InRange(frac, 0, 1);
        Assert.True(((NumberValue)c.Get("freeGB")!).Number >= 0);
    }
}

public class SourceFactoryTests
{
    [Fact]
    public void Creates_Known_Types_And_Rejects_Unknown()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        Assert.IsType<TimeSource>(SourceFactory.Create(new() { Name = "t", Type = "time" }, clock));
        var d = Assert.IsType<DisksSource>(SourceFactory.Create(new() { Name = "d", Type = "disks", EverySeconds = 120 }, clock));
        Assert.Equal(TimeSpan.FromSeconds(120), d.Every);
        Assert.Throws<NotSupportedException>(() => SourceFactory.Create(new() { Name = "x", Type = "gauge" }, clock));
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter FullyQualifiedName~Source`

- [ ] **Step 3: Implement**

`src/DeskWall.Core/Sources/IClock.cs`:

```csharp
namespace DeskWall.Core.Sources;

public interface IClock { DateTimeOffset Now { get; } }

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
}
```

`src/DeskWall.Core/Sources/TimeSource.cs`:

```csharp
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Publishes the local time. Due on every whole minute.</summary>
public sealed class TimeSource(string name, IClock clock) : ISource
{
    public string Name => name;

    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        var l = lastRefresh.Value;
        return new DateTimeOffset(l.Year, l.Month, l.Day, l.Hour, l.Minute, 0, l.Offset).AddMinutes(1);
    }

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var now = clock.Now;
        return new(new RecordValue(new Dictionary<string, Value>
        {
            ["now"] = new TimeValue(now),
            ["date"] = new TextValue(now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ["weekday"] = new TextValue(now.DayOfWeek.ToString()),
        }));
    }
}
```

`src/DeskWall.Core/Sources/DisksSource.cs`:

```csharp
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Fixed, ready drives. Bytes as numbers plus GB conveniences.</summary>
public sealed class DisksSource(string name, TimeSpan every, Func<IReadOnlyList<DriveInfo>>? drives = null)
    : PeriodicSource(name, every)
{
    private readonly Func<IReadOnlyList<DriveInfo>> _drives = drives ?? (() => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList());

    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var items = new List<RecordValue>();
        foreach (var d in _drives())
        {
            double free = d.AvailableFreeSpace, total = d.TotalSize;
            items.Add(new RecordValue(new Dictionary<string, Value>
            {
                ["letter"] = new TextValue(d.Name.TrimEnd('\\', ':')),
                ["label"] = new TextValue(d.VolumeLabel),
                ["free"] = new NumberValue(free),
                ["total"] = new NumberValue(total),
                ["freeGB"] = new NumberValue(Math.Round(free / 1e9)),
                ["totalGB"] = new NumberValue(Math.Round(total / 1e9)),
                ["usedFraction"] = new NumberValue(total <= 0 ? 0 : (total - free) / total),
            }));
        }
        return new(new RecordValue(new Dictionary<string, Value> { ["drives"] = new ListValue(items, "letter") }));
    }
}
```

`src/DeskWall.Core/Sources/SourceFactory.cs`:

```csharp
using DeskWall.Core.Layout;

namespace DeskWall.Core.Sources;

public static class SourceFactory
{
    public static ISource Create(SourceDef def, IClock clock)
    {
        var every = TimeSpan.FromSeconds(def.EverySeconds ?? DefaultEvery(def.Type));
        return def.Type.ToLowerInvariant() switch
        {
            "time" => new TimeSource(def.Name, clock),
            "disks" => new DisksSource(def.Name, every),
            // Phase 4 adds: http, rss, file, command, system
            _ => throw new NotSupportedException($"source type '{def.Type}' (source '{def.Name}')"),
        };
    }

    private static int DefaultEvery(string type) => type.ToLowerInvariant() switch
    {
        "disks" => 300,
        "system" => 900,
        _ => 600,
    };
}
```

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test --filter FullyQualifiedName~Source`
Expected: 4 passing.

- [ ] **Step 5: Commit**

```bash
git add src/DeskWall.Core/Sources tests/DeskWall.Core.Tests/Sources
git commit -m "Sources: time (minute-aligned), disks (fixed drives), factory, IClock"
```

---

### Task 7: Display signature and monitor enumeration (lane `v1/p1-sources`)

**Files:**
- Create: `src/DeskWall.Core/Display/DisplaySignature.cs`, `src/DeskWall.Core/Display/Monitors.cs`
- Modify: `src/DeskWall.Core/NativeMethods.txt` (append)
- Test: `tests/DeskWall.Core.Tests/Display/DisplaySignatureTests.cs`

**Interfaces:**
- Consumes: `Rect`.
- Produces:
  - `DisplaySignature(string DevicePath, int Width, int Height, int ScalePercent)` with `string Key` (`"{DevicePath} @ {W}x{H} @ {Scale}%"`), `static DisplaySignature Parse(string key)`, and `double Similarity(DisplaySignature other)` used by spec 5's closest-layout pick: 3 if same device path, plus 1 if same aspect ratio within 1 percent, plus 1 if same resolution.
  - `MonitorInfo(DisplaySignature Signature, Rect Bounds, bool IsPrimary, string WallpaperMonitorId)`.
  - `Monitors.Enumerate() : IReadOnlyList<MonitorInfo>` via `EnumDisplayMonitors` + `GetDpiForMonitor` + `IDesktopWallpaper.GetMonitorDevicePathAt/GetMonitorRECT` (the wallpaper API's id is what `SetWallpaper` needs per monitor; bounds from `GetMonitorRECT` are matched to `EnumDisplayMonitors` rects to join the two).

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core.Display;
using Xunit;

public class DisplaySignatureTests
{
    [Fact]
    public void Key_RoundTrips()
    {
        var s = new DisplaySignature(@"\\?\DISPLAY#DELA1F2#5&abc#0#UID4353", 3440, 1440, 100);
        Assert.Equal(@"\\?\DISPLAY#DELA1F2#5&abc#0#UID4353 @ 3440x1440 @ 100%", s.Key);
        Assert.Equal(s, DisplaySignature.Parse(s.Key));
    }

    [Fact]
    public void Similarity_Prefers_Same_Device_Then_Aspect()
    {
        var mine = new DisplaySignature("A", 3440, 1440, 100);
        Assert.Equal(5, mine.Similarity(mine));
        Assert.Equal(3, mine.Similarity(new DisplaySignature("A", 1920, 1080, 100)));
        Assert.Equal(1, mine.Similarity(new DisplaySignature("B", 2560, 1080, 100)));   // 21:9 vs 21.5:9 within 1 percent? no: 2.37 vs 2.39 -> yes within 1%
        Assert.Equal(0, mine.Similarity(new DisplaySignature("B", 1920, 1080, 100)));
    }

    [Fact]
    public void Enumerate_Returns_At_Least_Primary()
    {
        var mons = Monitors.Enumerate();
        Assert.Contains(mons, m => m.IsPrimary);
        var p = mons.First(m => m.IsPrimary);
        Assert.True(p.Bounds.W > 0 && p.Bounds.H > 0);
        Assert.False(string.IsNullOrEmpty(p.WallpaperMonitorId));
        Assert.Equal(p.Bounds.W, p.Signature.Width);
    }
}
```

(The `2560x1080` case: 2560/1080 = 2.370, 3440/1440 = 2.389, difference 0.8 percent, so
aspect matches. Keep the numbers; the comment in the test is the working.)

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter FullyQualifiedName~DisplaySignature`

- [ ] **Step 3: NativeMethods.txt additions**

Append to `src/DeskWall.Core/NativeMethods.txt`:

```
EnumDisplayMonitors
GetMonitorInfo
MONITORINFOEXW
GetDpiForMonitor
MONITOR_DPI_TYPE
IDesktopWallpaper
DesktopWallpaper
CoCreateInstance
CoInitializeEx
CoTaskMemFree
```

- [ ] **Step 4: Implement**

`src/DeskWall.Core/Display/DisplaySignature.cs`:

```csharp
using System.Globalization;

namespace DeskWall.Core.Display;

public sealed record DisplaySignature(string DevicePath, int Width, int Height, int ScalePercent)
{
    public string Key => $"{DevicePath} @ {Width}x{Height} @ {ScalePercent}%";
    public double Aspect => (double)Width / Height;

    public static DisplaySignature Parse(string key)
    {
        var parts = key.Split(" @ ");
        if (parts.Length != 3) throw new FormatException($"bad signature '{key}'");
        var wh = parts[1].Split('x');
        return new(parts[0], int.Parse(wh[0], CultureInfo.InvariantCulture), int.Parse(wh[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2].TrimEnd('%'), CultureInfo.InvariantCulture));
    }

    /// <summary>Higher is closer. 3 same device, +1 same aspect within 1 percent, +1 same resolution.</summary>
    public int Similarity(DisplaySignature o)
    {
        var s = 0;
        if (string.Equals(DevicePath, o.DevicePath, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (Math.Abs(Aspect - o.Aspect) / Aspect <= 0.01) s += 1;
        if (Width == o.Width && Height == o.Height) s += 1;
        return s;
    }
}

public sealed record MonitorInfo(DisplaySignature Signature, Rect Bounds, bool IsPrimary, string WallpaperMonitorId);
```

`src/DeskWall.Core/Display/Monitors.cs`:

```csharp
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
        IDesktopWallpaper* dw;
        PInvoke.CoCreateInstance(typeof(DesktopWallpaper).GUID, null, CLSCTX.CLSCTX_LOCAL_SERVER, out dw).ThrowOnFailure();
        try
        {
            uint count; dw->GetMonitorDevicePathCount(&count);
            var result = new List<MonitorInfo>();
            for (uint i = 0; i < count; i++)
            {
                PWSTR id; dw->GetMonitorDevicePathAt(i, &id);
                string idStr = id.ToString(); PInvoke.CoTaskMemFree(id);
                RECT rc; if (dw->GetMonitorRECT(id, &rc).Failed) continue;   // detached monitor
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

    [UnmanagedCallersOnly]
    private static BOOL Callback(HMONITOR mon, HDC hdc, RECT* rc, LPARAM lp)
    {
        var raws = (List<Raw>)GCHandle.FromIntPtr(lp).Target!;
        var mi = new MONITORINFOEXW(); mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        PInvoke.GetMonitorInfo(mon, (MONITORINFO*)&mi);
        uint dx, dy; PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dx, &dy);
        var r = mi.monitorInfo.rcMonitor;
        raws.Add(new Raw(new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top), (mi.monitorInfo.dwFlags & 1) != 0, (int)dx, mi.szDevice.ToString()));
        return true;
    }
}

/// <summary>Per-thread COM init, idempotent.</summary>
public static class Com
{
    [ThreadStatic] private static bool _done;
    public static void EnsureInitialized()
    {
        if (_done) return;
        PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);   // S_FALSE if already; ignore
        _done = true;
    }
}
```

Adjust names to what CsWin32 emits (the spike results file lists the `IDesktopWallpaper`
shapes). `GetMonitorRECT` fails for a monitor id that is no longer attached; skipping it is
the intended behaviour.

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test --filter FullyQualifiedName~DisplaySignature`
Expected: 3 passing. Expected on JOES-PC: one primary monitor, 3440x1440, scale 100.

- [ ] **Step 6: Commit**

```bash
git add src/DeskWall.Core/Display src/DeskWall.Core/NativeMethods.txt tests/DeskWall.Core.Tests/Display
git commit -m "Display: signature key/similarity and monitor enumeration joined with IDesktopWallpaper ids"
```

Lane `v1/p1-sources` is complete: rebase on `v1`, `dotnet test`, fast-forward merge.

---

### Task 8: `Surface`, `BaseCache` and `FrameRenderer` (lane `v1/p1-render`)

Written against the spike results file. Copy the verified call shapes from there; the code
below shows the intended structure and the public API, which is frozen.

**Files:**
- Create: `src/DeskWall.Core/Render/Surface.cs`, `src/DeskWall.Core/Render/BaseCache.cs`,
  `src/DeskWall.Core/Render/FrameRenderer.cs`
- Modify: `src/DeskWall.Core/NativeMethods.txt` (append the spike's list)
- Test: `tests/DeskWall.Core.Tests/Render/SurfaceTests.cs`, `tests/DeskWall.Core.Tests/Render/FrameRendererTests.cs`

**Interfaces:**
- Consumes: `Resolved*` records, `TextStyle`, `Color`, `Rect`, `Fit` (Task 2).
- Produces:

```csharp
public sealed unsafe class Surface : IDisposable
{
    public int Width { get; } public int Height { get; }
    public static Surface Create(int width, int height);            // transparent, premultiplied BGRA
    public static Surface Load(string path);                         // any WIC-decodable image, converted to PBGRA
    public static Surface LoadRaw(string path);                      // BaseCache format
    public void SaveRaw(string path);                                // 16-byte header (magic, w, h) + pixels
    public void SaveJpeg(string path, int quality);                  // atomic: writes .tmp then moves
    public void SavePng(string path);
    public void Clear(Color c);
    public void FillRect(Rect r, Color c, float radius = 0);
    public void DrawSurface(Surface src, Rect dst, Fit fit, float opacity = 1, float radius = 0);
    public void DrawText(string text, TextStyle style, Rect rect);
    public void CopyRect(Surface src, Rect r);                       // same-size surfaces; for Task 11
    public (byte A, byte R, byte G, byte B) GetPixel(int x, int y);  // tests only; slow
}

public static class BaseCache
{
    /// <summary>Returns the raw-cache path for (image, size, fit), building it if missing or stale.</summary>
    public static string Ensure(string imagePath, int w, int h, Fit fit);
}

public sealed class FrameRenderer
{
    public FrameRenderer(int width, int height);
    /// <summary>Full render: base then every component in z-order. Returns the frame.</summary>
    public Surface RenderAll(string baseRawPath, IReadOnlyList<Resolved> components);
    /// <summary>Draw one component into an existing frame.</summary>
    public static void Draw(Surface frame, Resolved c);
}
```

Cover fit: scale so the image fills `dst`, centre-crop. Contain: fit inside, centre, leave
transparent. Text effect `Shadow`: draw text in `EffectColor` offset by (1, 1) through a
Gaussian blur of `EffectRadius`, then the text; `Outline`: DirectWrite geometry stroked in
`EffectColor` 1.5 px; `Plate`: fill the text's measured bounds inflated by 8 px with
`EffectColor` and `EffectRadius` corner radius, then the text. Gaussian blur uses the
Direct2D effect pipeline, which requires an `ID2D1DeviceContext`; the software WIC render
target supports `QueryInterface` to `ID2D1DeviceContext` on Windows 8 and later, still with no
D3D device. If the spike shows it does not, `Shadow` falls back to two offset draws at 40
percent alpha and the plan records the decision.

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using Xunit;

public class SurfaceTests
{
    [Fact]
    public void Create_Clear_FillRect_GetPixel()
    {
        using var s = Surface.Create(64, 32);
        s.Clear(new Color(255, 10, 20, 30));
        s.FillRect(new Rect(8, 8, 16, 8), new Color(255, 200, 100, 50));
        Assert.Equal(((byte)255, (byte)10, (byte)20, (byte)30), s.GetPixel(0, 0));
        Assert.Equal(((byte)255, (byte)200, (byte)100, (byte)50), s.GetPixel(10, 10));
    }

    [Fact]
    public void Jpeg_And_Raw_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests"); Directory.CreateDirectory(dir);
        using var s = Surface.Create(100, 50);
        s.Clear(new Color(255, 0, 128, 255));
        var jpg = Path.Combine(dir, "t.jpg"); s.SaveJpeg(jpg, 92);
        Assert.True(new FileInfo(jpg).Length > 500);
        using var back = Surface.Load(jpg);
        Assert.Equal((100, 50), (back.Width, back.Height));
        var p = back.GetPixel(50, 25);
        Assert.InRange(p.G, 120, 136);
        var raw = Path.Combine(dir, "t.raw"); s.SaveRaw(raw);
        using var raw2 = Surface.LoadRaw(raw);
        Assert.Equal(s.GetPixel(3, 3), raw2.GetPixel(3, 3));
    }

    [Fact]
    public void DrawText_Marks_Pixels()
    {
        using var s = Surface.Create(200, 80);
        s.Clear(new Color(255, 0, 0, 0));
        s.DrawText("14:32", TextStyle.Default with { Size = 48, Effect = TextEffect.None }, new Rect(0, 0, 200, 80));
        var lit = 0;
        for (var y = 0; y < 80; y += 2) for (var x = 0; x < 200; x += 2) if (s.GetPixel(x, y).R > 128) lit++;
        Assert.InRange(lit, 100, 3000);
    }
}

public class FrameRendererTests
{
    [Fact]
    public void RenderAll_Draws_Base_Then_Components_By_Z()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests"); Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "base.png");
        using (var b = Surface.Create(20, 20)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(basePng); }
        var raw = BaseCache.Ensure(basePng, 40, 20, Fit.Cover);
        var r = new FrameRenderer(40, 20);
        var comps = new Resolved[]
        {
            new ResolvedBar("b", new Rect(0, 0, 40, 20), 2, 0.5, new Color(255, 0, 0, 0), new Color(255, 255, 0, 0), Axis.Horizontal),
            new ResolvedBar("under", new Rect(0, 0, 40, 20), 1, 1.0, new Color(255, 0, 255, 0), new Color(255, 0, 255, 0), Axis.Horizontal),
        };
        using var frame = r.RenderAll(raw, comps);
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), frame.GetPixel(5, 10));   // fill half of top bar
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)0), frame.GetPixel(35, 10));    // track of top bar covers the green one
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter "FullyQualifiedName~SurfaceTests|FullyQualifiedName~FrameRendererTests"`

- [ ] **Step 3: Implement `Surface`**

Structure (fill in the call shapes from the spike results):

```csharp
using Windows.Win32;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Imaging;

namespace DeskWall.Core.Render;

public sealed unsafe class Surface : IDisposable
{
    // Process-wide factories, created once per process on first use. Cheap to keep: no GPU,
    // just two COM objects. Released at process exit.
    private static IWICImagingFactory2* s_wic;
    private static ID2D1Factory* s_d2d;
    private static IDWriteFactory* s_dw;

    private IWICBitmap* _bmp;
    private ID2D1RenderTarget* _rt;
    public int Width { get; }
    public int Height { get; }

    private Surface(IWICBitmap* bmp, int w, int h) { _bmp = bmp; Width = w; Height = h; }

    private static void EnsureFactories() { /* CoInitializeEx, CoCreateInstance WIC, D2D1CreateFactory, DWriteCreateFactory as in the spike */ }

    public static Surface Create(int width, int height)
    {
        EnsureFactories();
        IWICBitmap* bmp;
        s_wic->CreateBitmap((uint)width, (uint)height, PInvoke.GUID_WICPixelFormat32bppPBGRA, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand, &bmp).ThrowOnFailure();
        return new Surface(bmp, width, height);
    }

    public static Surface Load(string path)
    {
        EnsureFactories();
        // CreateDecoderFromFilename -> GetFrame(0) -> CreateFormatConverter to PBGRA -> CreateBitmapFromSource(WICBitmapCacheOnLoad)
        // ...
    }

    private ID2D1RenderTarget* Rt()
    {
        if (_rt is null)
        {
            var props = new D2D1_RENDER_TARGET_PROPERTIES { type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE, /* PBGRA premultiplied, 96 dpi */ };
            s_d2d->CreateWicBitmapRenderTarget(_bmp, &props, &_rt).ThrowOnFailure();
        }
        return _rt;
    }

    private void Draw(Action<IntPtr> body)   // BeginDraw / body / EndDraw with HRESULT check
    {
        var rt = Rt(); rt->BeginDraw();
        try { body((IntPtr)rt); }
        finally { rt->EndDraw(null, null).ThrowOnFailure(); }
    }

    public void Clear(Color c) => Draw(p => ((ID2D1RenderTarget*)p)->Clear(ToD2D(c)));

    public void FillRect(Rect r, Color c, float radius = 0) => Draw(p =>
    {
        var rt = (ID2D1RenderTarget*)p;
        ID2D1SolidColorBrush* brush; rt->CreateSolidColorBrush(ToD2D(c), null, &brush).ThrowOnFailure();
        var rf = ToD2D(r);
        if (radius <= 0) rt->FillRectangle(&rf, (ID2D1Brush*)brush);
        else { var rr = new D2D1_ROUNDED_RECT { rect = rf, radiusX = radius, radiusY = radius }; rt->FillRoundedRectangle(&rr, (ID2D1Brush*)brush); }
        brush->Release();
    });

    public void DrawSurface(Surface src, Rect dst, Fit fit, float opacity = 1, float radius = 0) => Draw(p =>
    {
        var rt = (ID2D1RenderTarget*)p;
        ID2D1Bitmap* bmp; rt->CreateBitmapFromWicBitmap((IWICBitmapSource*)src._bmp, null, &bmp).ThrowOnFailure();
        var (srcRect, dstRect) = FitRects(src.Width, src.Height, dst, fit);
        if (radius > 0) { /* PushLayer with a rounded-rect geometry mask, draw, PopLayer */ }
        rt->DrawBitmap(bmp, &dstRect, opacity, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, &srcRect);
        if (radius > 0) rt->PopLayer();
        bmp->Release();
    });

    /// <summary>Cover: crop source to dst aspect. Contain: shrink dst to source aspect. Stretch: both full.</summary>
    internal static (D2D_RECT_F src, D2D_RECT_F dst) FitRects(int sw, int sh, Rect dst, Fit fit)
    {
        double sa = (double)sw / sh, da = (double)dst.W / dst.H;
        switch (fit)
        {
            case Fit.Stretch: return (new(0, 0, sw, sh), ToD2D(dst));
            case Fit.Cover:
                if (sa > da) { var w = sh * da; var x = (sw - w) / 2; return (new((float)x, 0, (float)(x + w), sh), ToD2D(dst)); }
                else { var h = sw / da; var y = (sh - h) / 2; return (new(0, (float)y, sw, (float)(y + h)), ToD2D(dst)); }
            default: // Contain
                if (sa > da) { var h = dst.W / sa; var y = dst.Y + (dst.H - h) / 2; return (new(0, 0, sw, sh), new(dst.X, (float)y, dst.Right, (float)(y + h))); }
                else { var w = dst.H * sa; var x = dst.X + (dst.W - w) / 2; return (new(0, 0, sw, sh), new((float)x, dst.Y, (float)(x + w), dst.Bottom)); }
        }
    }

    public void DrawText(string text, TextStyle style, Rect rect) => Draw(p =>
    {
        var rt = (ID2D1RenderTarget*)p;
        // CreateTextFormat(style.Font, weight, size) ; SetTextAlignment from style.Align ; SetParagraphAlignment NEAR
        // Effect: Shadow -> draw EffectColor at (+1,+1) through the blur path described above; Outline; Plate. Then the text in style.Color.
        // Record which shadow path was used in a comment here once the spike result is known.
    });

    public void CopyRect(Surface src, Rect r) => Draw(p =>
    {
        var rt = (ID2D1RenderTarget*)p;
        ID2D1Bitmap* bmp; rt->CreateBitmapFromWicBitmap((IWICBitmapSource*)src._bmp, null, &bmp).ThrowOnFailure();
        var rf = ToD2D(r);
        rt->DrawBitmap(bmp, &rf, 1f, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR, &rf);
        bmp->Release();
    });

    public void SaveJpeg(string path, int quality) { /* spike encoder path with ImageQuality = quality / 100f; write to path + ".tmp"; File.Move overwrite */ }
    public void SavePng(string path) { /* same with GUID_ContainerFormatPng, no property */ }

    public void SaveRaw(string path)
    {
        // Lock the WIC bitmap (IWICBitmap::Lock, WICBitmapLockRead), write "DWRAW1\0\0" + int32 w + int32 h + stride*h bytes.
    }
    public static Surface LoadRaw(string path) { /* Create(w,h), Lock write, copy */ }

    public (byte A, byte R, byte G, byte B) GetPixel(int x, int y) { /* Lock read one row, index x*4: B,G,R,A premultiplied; un-premultiply */ }

    private static D2D1_COLOR_F ToD2D(Color c) => new() { r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f };
    private static D2D_RECT_F ToD2D(Rect r) => new() { left = r.X, top = r.Y, right = r.Right, bottom = r.Bottom };

    public void Dispose()
    {
        if (_rt is not null) { _rt->Release(); _rt = null; }
        if (_bmp is not null) { _bmp->Release(); _bmp = null; }
    }
}
```

Every `/* ... */` above is a place where the spike results file has the verified code; paste
it. Nothing else is left open.

- [ ] **Step 4: Implement `BaseCache` and `FrameRenderer`**

`src/DeskWall.Core/Render/BaseCache.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace DeskWall.Core.Render;

/// <summary>The base image scaled to the canvas, stored as a raw PBGRA dump so loading it is a
/// copy, not a decode. Keyed by path, mtime, size and fit.</summary>
public static class BaseCache
{
    public static string Ensure(string imagePath, int w, int h, Fit fit)
    {
        var mtime = File.GetLastWriteTimeUtc(imagePath).Ticks;
        var keySrc = $"{imagePath}|{mtime}|{w}x{h}|{fit}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySrc)))[..16];
        var path = Paths.InRuntime("base", $"{key}.raw");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var src = Surface.Load(imagePath);
        using var dst = Surface.Create(w, h);
        dst.Clear(new Color(255, 0, 0, 0));
        dst.DrawSurface(src, new Rect(0, 0, w, h), fit);
        dst.SaveRaw(path);
        // keep the cache dir tidy: drop other .raw files for this canvas size older than a day
        foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.raw"))
            if (f != path && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f);
        return path;
    }
}
```

`src/DeskWall.Core/Render/FrameRenderer.cs`:

```csharp
using DeskWall.Core.Resolve;

namespace DeskWall.Core.Render;

public sealed class FrameRenderer(int width, int height)
{
    public int Width => width;
    public int Height => height;

    public Surface RenderAll(string baseRawPath, IReadOnlyList<Resolved> components)
    {
        var frame = Surface.LoadRaw(baseRawPath);
        if (frame.Width != width || frame.Height != height) throw new InvalidOperationException("base cache size mismatch");
        foreach (var c in components.OrderBy(c => c.Z)) Draw(frame, c);
        return frame;
    }

    public static void Draw(Surface frame, Resolved c)
    {
        switch (c)
        {
            case ResolvedText t:
                frame.DrawText(t.Text, t.Style, t.Rect);
                break;
            case ResolvedImage i:
                if (!File.Exists(i.Path)) { frame.FillRect(i.Rect, new Color(140, 0, 0, 0), i.Radius); break; }
                using (var img = Surface.Load(i.Path)) frame.DrawSurface(img, i.Rect, i.Fit, i.Opacity, i.Radius);
                break;
            case ResolvedBar b:
                frame.FillRect(b.Rect, b.Track);
                var f = Math.Clamp(b.Fraction, 0, 1);
                var fill = b.Direction == Axis.Horizontal
                    ? new Rect(b.Rect.X, b.Rect.Y, (int)Math.Round(b.Rect.W * f), b.Rect.H)
                    : new Rect(b.Rect.X, b.Rect.Bottom - (int)Math.Round(b.Rect.H * f), b.Rect.W, (int)Math.Round(b.Rect.H * f));
                if (fill.W > 0 && fill.H > 0) frame.FillRect(fill, b.Fill);
                break;
            case ResolvedShortcut:
                break;   // draws nothing; Phase 3 owns the icon
        }
    }
}
```

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test --filter "FullyQualifiedName~SurfaceTests|FullyQualifiedName~FrameRendererTests"`
Expected: 4 passing. Then `dotnet publish src/DeskWall.Daemon -c Release -r win-x64`: zero IL warnings.

- [ ] **Step 6: Commit**

```bash
git add src/DeskWall.Core/Render src/DeskWall.Core/NativeMethods.txt tests/DeskWall.Core.Tests/Render
git commit -m "Render: Surface over software Direct2D/WIC, raw base cache, FrameRenderer"
```

---

### Task 9: `WallpaperSetter` (lane `v1/p1-render`)

**Files:**
- Create: `src/DeskWall.Core/Wallpaper/WallpaperSetter.cs`
- Test: `tests/DeskWall.Core.Tests/Wallpaper/WallpaperSetterTests.cs`

**Interfaces:**
- Consumes: `Com.EnsureInitialized` (Task 7), `MonitorInfo.WallpaperMonitorId` (Task 7).
- Produces:
  - `WallpaperSetter.Set(string monitorId, string imagePath)`: `IDesktopWallpaper::SetWallpaper(monitorId, path)` after `SetPosition(DWPOS_FILL)`.
  - `WallpaperSetter.Get(string monitorId) : string?`: current path.
  - `WallpaperSetter.RecordRestorePoint()`: writes `restore.json` in the runtime dir with `{ monitorId: path }` for every monitor, only if the file does not already exist. `Restore()` reads it and sets each, then deletes it. Used by `uninstall` in Phase 2.

- [ ] **Step 1: Failing test**

```csharp
using DeskWall.Core.Display;
using DeskWall.Core.Wallpaper;
using Xunit;

public class WallpaperSetterTests
{
    [Fact]
    public void Get_ReturnsCurrentPath_ForPrimary()
    {
        var p = Monitors.Enumerate().First(m => m.IsPrimary);
        var current = WallpaperSetter.Get(p.WallpaperMonitorId);
        Assert.False(string.IsNullOrEmpty(current));
    }

    [Fact]
    public void RecordRestorePoint_WritesOnce()
    {
        var path = DeskWall.Core.Paths.InRuntime("restore.json");
        var existed = File.Exists(path);
        WallpaperSetter.RecordRestorePoint();
        Assert.True(File.Exists(path));
        if (!existed) Assert.Contains("\"", File.ReadAllText(path));
    }
}
```

(No test sets the wallpaper: that is what `tick` does in Task 10 on the real desktop.)

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter FullyQualifiedName~WallpaperSetter`

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Display;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace DeskWall.Core.Wallpaper;

public static unsafe class WallpaperSetter
{
    private static IDesktopWallpaper* Open()
    {
        Com.EnsureInitialized();
        IDesktopWallpaper* dw;
        PInvoke.CoCreateInstance(typeof(DesktopWallpaper).GUID, null, CLSCTX.CLSCTX_LOCAL_SERVER, out dw).ThrowOnFailure();
        return dw;
    }

    public static void Set(string monitorId, string imagePath)
    {
        var dw = Open();
        try
        {
            dw->SetPosition(DESKTOP_WALLPAPER_POSITION.DWPOS_FILL);
            fixed (char* m = monitorId) fixed (char* p = imagePath) dw->SetWallpaper(m, p).ThrowOnFailure();
        }
        finally { dw->Release(); }
    }

    public static string? Get(string monitorId)
    {
        var dw = Open();
        try
        {
            PWSTR path;
            fixed (char* m = monitorId) if (dw->GetWallpaper(m, &path).Failed) return null;
            var s = path.ToString(); PInvoke.CoTaskMemFree(path);
            return s;
        }
        finally { dw->Release(); }
    }

    public static void RecordRestorePoint()
    {
        var file = Paths.InRuntime("restore.json");
        if (File.Exists(file)) return;
        var map = new Dictionary<string, string>();
        foreach (var m in Monitors.Enumerate()) if (Get(m.WallpaperMonitorId) is { } p) map[m.WallpaperMonitorId] = p;
        File.WriteAllText(file, JsonSerializer.Serialize(map, RestoreJsonContext.Default.DictionaryStringString));
    }

    public static void Restore()
    {
        var file = Paths.InRuntime("restore.json");
        if (!File.Exists(file)) return;
        var map = JsonSerializer.Deserialize(File.ReadAllText(file), RestoreJsonContext.Default.DictionaryStringString) ?? new();
        foreach (var (id, path) in map) if (File.Exists(path)) Set(id, path);
        File.Delete(file);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class RestoreJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test --filter FullyQualifiedName~WallpaperSetter`
Expected: 2 passing.

- [ ] **Step 5: Commit**

```bash
git add src/DeskWall.Core/Wallpaper tests/DeskWall.Core.Tests/Wallpaper
git commit -m "Wallpaper: per-monitor IDesktopWallpaper set/get and restore point"
```

Lane `v1/p1-render` is complete: rebase on `v1`, `dotnet test`, fast-forward merge.

---

### Task 6: `LayoutResolver` and `ContentKey` (integrator, on `v1` after all lanes merge)

**Files:**
- Create: `src/DeskWall.Core/Resolve/LayoutResolver.cs`, `src/DeskWall.Core/Resolve/ContentKey.cs`,
  `src/DeskWall.Core/Resolve/PropertyReader.cs`
- Test: `tests/DeskWall.Core.Tests/Resolve/LayoutResolverTests.cs`, `tests/DeskWall.Core.Tests/Resolve/ContentKeyTests.cs`

**Interfaces:**
- Consumes: `LayoutFile`, `*Def`, `PropertyValue` (Task 4); `BindingResolver` (Task 3); `Resolved*`, `TextStyle`, `Color` (Task 2); `Surface.Load` for `cellHeight: auto` (Task 8).
- Produces:
  - `LayoutResolver.Resolve(LayoutFile layout, RecordValue tree, Rect canvas) : IReadOnlyList<Resolved>`; every result has `ContentKey` set; repeaters are expanded; rects are absolute.
  - `ContentKey.Of(Resolved) : string` (16 hex chars of SHA-256 over type name, rect, z and `KeyParts()` joined with `\u001F`).
  - `PropertyReader`: `Text(PropertyValue, RecordValue scope) : string?`, `Number(...) : double?`, `Color(...)`, `Enum<T>(...)`. A bound property whose binding resolves to null yields null; callers substitute the fallback documented per component below.

Fallbacks when a binding is missing: `text` renders `""`; `image` renders the "missing"
plate (Task 8 already does that for a nonexistent path, so pass `""`); `bar` uses fraction 0;
`shortcut` with a missing target is dropped entirely (no icon for nothing); `repeater` with a
missing list expands to nothing.

Repeater expansion: for item i, the cell origin is `rect.X, rect.Y + i * (cellH + gap)`
(vertical) or `rect.X + i * (cellW + gap), rect.Y` (horizontal). `cellHeight` (or width for
horizontal) is the literal number, or for `auto`: the first `ImageDef` child's resolved path
is loaded and the cell height is `child.Rect.W * imgH / imgW`; the image child's rect height is
set to that too. Children whose cell would overflow the repeater rect are not emitted
(spec: never draw into the block below). Child ids become `{repeaterId}[{i}].{childId}`.
Child shortcut slot = `child.Slot + i`.

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

public class LayoutResolverTests
{
    private static RecordValue Tree()
    {
        RecordValue Drive(string l, double frac) => new(new Dictionary<string, Value> { ["letter"] = new TextValue(l), ["freeGB"] = new NumberValue(100), ["usedFraction"] = new NumberValue(frac) });
        var disks = new RecordValue(new Dictionary<string, Value> { ["drives"] = new ListValue([Drive("C", 0.5), Drive("D", 0.9), Drive("E", 0.1)], "letter") });
        var time = new RecordValue(new Dictionary<string, Value> { ["now"] = new TimeValue(new DateTimeOffset(2026, 9, 20, 14, 32, 0, TimeSpan.Zero)) });
        return ValueTree.Of(("disks", disks), ("time", time));
    }

    private static LayoutFile Layout() => LayoutFile.Parse("""
    {
      "version": 1, "baseImage": "x.jpg", "sources": [],
      "components": [
        { "type": "text", "id": "clock", "rect": [100, 10, 172, 70], "z": 1, "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "align": "right" },
        { "type": "repeater", "id": "drives", "rect": [100, 200, 172, 92], "items": { "bind": "disks.drives" }, "axis": "vertical", "gap": 0, "cellHeight": 46,
          "template": [
            { "type": "text", "id": "letter", "rect": [0, 0, 60, 24], "text": { "bind": "letter | \"{0}:\"" } },
            { "type": "bar", "id": "bar", "rect": [0, 26, 172, 6], "fraction": { "bind": "usedFraction" }, "threshold": 0.85, "thresholdFill": "#FFD13438" },
            { "type": "shortcut", "id": "go", "rect": [0, 0, 172, 46], "slot": 10, "target": { "bind": "letter | \"explorer.exe {0}:\\\\\"" } }
          ] },
        { "type": "text", "id": "missing", "rect": [0, 0, 10, 10], "text": { "bind": "nope.value" } },
        { "type": "shortcut", "id": "nolink", "rect": [0, 0, 10, 10], "target": { "bind": "nope.value" } }
      ]
    }
    """);

    [Fact]
    public void Resolves_Text_With_Style()
    {
        var r = LayoutResolver.Resolve(Layout(), Tree(), new Rect(0, 0, 3440, 1440));
        var clock = Assert.IsType<ResolvedText>(r.Single(c => c.Id == "clock"));
        Assert.Equal("14:32", clock.Text);
        Assert.Equal("Segoe UI Light", clock.Style.Font);
        Assert.Equal(64f, clock.Style.Size);
        Assert.Equal(Align.Right, clock.Style.Align);
        Assert.Equal(16, clock.ContentKey.Length);
    }

    [Fact]
    public void Expands_Repeater_Within_Bounds_And_Assigns_Slots()
    {
        var r = LayoutResolver.Resolve(Layout(), Tree(), new Rect(0, 0, 3440, 1440));
        // 92 px tall, 46 px cells: only 2 of 3 drives fit
        var letters = r.OfType<ResolvedText>().Where(t => t.Id.StartsWith("drives[")).ToList();
        Assert.Equal(["C:", "D:"], letters.Select(t => t.Text));
        Assert.Equal(new Rect(100, 246, 60, 24), letters[1].Rect);
        var bars = r.OfType<ResolvedBar>().ToList();
        Assert.Equal(Color.Parse("#EBFFFFFF"), bars[0].Fill);   // 0.5 < 0.85: normal fill
        Assert.Equal(Color.Parse("#FFD13438"), bars[1].Fill);                                                               // 0.9 >= 0.85: threshold fill
        var scs = r.OfType<ResolvedShortcut>().ToList();
        Assert.Equal([10, 11], scs.Select(s => s.Slot));
        Assert.Equal(@"explorer.exe D:\", scs[1].Target);
    }

    [Fact]
    public void Missing_Bindings_Fallback()
    {
        var r = LayoutResolver.Resolve(Layout(), Tree(), new Rect(0, 0, 3440, 1440));
        Assert.Equal("", ((ResolvedText)r.Single(c => c.Id == "missing")).Text);
        Assert.DoesNotContain(r, c => c.Id == "nolink");
    }
}

public class ContentKeyTests
{
    [Fact]
    public void Key_Changes_With_Content_Not_With_Identity()
    {
        var a = new ResolvedText("x", new Rect(0, 0, 10, 10), 0, "14:32", TextStyle.Default);
        var b = new ResolvedText("y", new Rect(0, 0, 10, 10), 0, "14:32", TextStyle.Default);
        var c = new ResolvedText("x", new Rect(0, 0, 10, 10), 0, "14:33", TextStyle.Default);
        var d = new ResolvedText("x", new Rect(1, 0, 10, 10), 0, "14:32", TextStyle.Default);
        Assert.Equal(ContentKey.Of(a), ContentKey.Of(b));
        Assert.NotEqual(ContentKey.Of(a), ContentKey.Of(c));
        Assert.NotEqual(ContentKey.Of(a), ContentKey.Of(d));
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter FullyQualifiedName~Resolve`

- [ ] **Step 3: Implement**

`src/DeskWall.Core/Resolve/ContentKey.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace DeskWall.Core.Resolve;

public static class ContentKey
{
    public static string Of(Resolved c)
    {
        var sb = new StringBuilder();
        sb.Append(c.GetType().Name).Append('\u001F')
          .Append(c.Rect.X).Append(',').Append(c.Rect.Y).Append(',').Append(c.Rect.W).Append(',').Append(c.Rect.H).Append('\u001F')
          .Append(c.Z);
        foreach (var p in c.KeyParts()) sb.Append('\u001F').Append(p);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
```

`src/DeskWall.Core/Resolve/PropertyReader.cs`:

```csharp
using System.Globalization;
using DeskWall.Core.Binding;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Resolve;

/// <summary>Turns a PropertyValue into a typed value against a scope record (the tree, or a
/// repeater item). Null means "binding did not resolve"; the caller picks the fallback.</summary>
public static class PropertyReader
{
    public static string? Text(PropertyValue p, RecordValue scope)
        => p.IsBound ? BindingResolver.ResolveText(p.Binding!, scope) : p.LiteralText;

    public static double? Number(PropertyValue p, RecordValue scope)
    {
        if (p.IsBound)
            return BindingResolver.Resolve(p.Binding!, scope) switch
            {
                NumberValue n => n.Number,
                TextValue t when double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                BoolValue b => b.Flag ? 1 : 0,
                _ => null,
            };
        return double.TryParse(p.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static Render.Color? Color(PropertyValue p, RecordValue scope)
    {
        var s = Text(p, scope);
        if (s is null) return null;
        try { return Render.Color.Parse(s); } catch (FormatException) { return null; }
    }

    public static T? Enum<T>(PropertyValue p, RecordValue scope) where T : struct, System.Enum
    {
        var s = Text(p, scope);
        return s is not null && System.Enum.TryParse<T>(s, ignoreCase: true, out var e) ? e : null;
    }
}
```

`src/DeskWall.Core/Resolve/LayoutResolver.cs`:

```csharp
using DeskWall.Core.Binding;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Values;

namespace DeskWall.Core.Resolve;

public static class LayoutResolver
{
    public static IReadOnlyList<Resolved> Resolve(LayoutFile layout, RecordValue tree, Rect canvas)
    {
        var out_ = new List<Resolved>();
        foreach (var def in layout.Components) Emit(def, tree, tree, def.Rect, def.Id, 0, out_);
        for (var i = 0; i < out_.Count; i++) out_[i] = out_[i] with { ContentKey = ContentKey.Of(out_[i]) };
        return out_;
    }

    /// <param name="scope">record bindings resolve against (tree, or a repeater item)</param>
    /// <param name="rect">absolute rect for this instance</param>
    private static void Emit(ComponentDef def, RecordValue tree, RecordValue scope, Rect rect, string id, int slotOffset, List<Resolved> out_)
    {
        switch (def)
        {
            case TextDef t:
                out_.Add(new ResolvedText(id, rect, def.Z, PropertyReader.Text(t.Text, scope) ?? "", new TextStyle(
                    Font: PropertyReader.Text(t.Font, scope) ?? "Segoe UI",
                    Size: (float)(PropertyReader.Number(t.Size, scope) ?? 16),
                    Weight: (int)(PropertyReader.Number(t.Weight, scope) ?? 400),
                    Color: PropertyReader.Color(t.Color, scope) ?? Color.White,
                    Align: PropertyReader.Enum<Align>(t.Align, scope) ?? Align.Left,
                    Effect: PropertyReader.Enum<TextEffect>(t.Effect, scope) ?? TextEffect.Shadow,
                    EffectRadius: (float)(PropertyReader.Number(t.EffectRadius, scope) ?? 6),
                    EffectColor: PropertyReader.Color(t.EffectColor, scope) ?? new Color(160, 0, 0, 0))));
                break;

            case ImageDef i:
                out_.Add(new ResolvedImage(id, rect, def.Z, PropertyReader.Text(i.Source, scope) ?? "",
                    PropertyReader.Enum<Fit>(i.Fit, scope) ?? Fit.Cover,
                    (float)(PropertyReader.Number(i.Radius, scope) ?? 0),
                    (float)(PropertyReader.Number(i.Opacity, scope) ?? 1)));
                break;

            case BarDef b:
                var frac = PropertyReader.Number(b.Fraction, scope) ?? 0;
                var threshold = PropertyReader.Number(b.Threshold, scope) ?? 1;
                var fill = frac >= threshold
                    ? PropertyReader.Color(b.ThresholdFill, scope) ?? Color.Parse("#FFD13438")
                    : PropertyReader.Color(b.Fill, scope) ?? Color.Parse("#EBFFFFFF");
                out_.Add(new ResolvedBar(id, rect, def.Z, frac,
                    PropertyReader.Color(b.Track, scope) ?? Color.Parse("#46FFFFFF"), fill,
                    PropertyReader.Enum<Axis>(b.Direction, scope) ?? Axis.Horizontal));
                break;

            case ShortcutDef s:
                var target = PropertyReader.Text(s.Target, scope);
                if (string.IsNullOrWhiteSpace(target)) break;   // nothing to launch: no icon
                out_.Add(new ResolvedShortcut(id, rect, def.Z, target, PropertyReader.Text(s.Tooltip, scope) ?? "", s.Slot + slotOffset));
                break;

            case RepeaterDef r:
                if (BindingResolver.Resolve(r.Items.Binding ?? throw new InvalidOperationException($"repeater '{id}' items must be a binding"), scope) is not ListValue list) break;
                var vertical = r.Axis == Axis.Vertical;
                var cursor = 0;
                for (var idx = 0; idx < list.Items.Count; idx++)
                {
                    var item = list.Items[idx];
                    var cell = CellSize(r, item, scope);
                    var extent = vertical ? cell : cell;   // height for vertical, width for horizontal
                    if (cursor + extent > (vertical ? rect.H : rect.W)) break;   // never overflow the block
                    var origin = vertical ? rect.Offset(0, cursor) : rect.Offset(cursor, 0);
                    foreach (var child in r.Template)
                    {
                        var childRect = child.Rect.Offset(origin.X, origin.Y);
                        if (child is ImageDef && IsAuto(r.CellHeight) && vertical) childRect = childRect with { H = cell };
                        Emit(child, tree, item, childRect, $"{id}[{idx}].{child.Id}", slotOffset + idx, out_);
                    }
                    cursor += extent + r.Gap;
                }
                break;
        }
    }

    private static bool IsAuto(PropertyValue p) => !p.IsBound && string.Equals(p.LiteralText, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cell extent along the axis: literal, or from the first image child's aspect ratio.</summary>
    private static int CellSize(RepeaterDef r, RecordValue item, RecordValue scope)
    {
        if (!IsAuto(r.CellHeight)) return (int)Math.Round(PropertyReader.Number(r.CellHeight, item) ?? 0);
        var img = r.Template.OfType<ImageDef>().FirstOrDefault();
        if (img is null) return r.Template.Count == 0 ? 0 : r.Template.Max(c => c.Rect.Bottom);
        var path = PropertyReader.Text(img.Source, item);
        if (path is null || !File.Exists(path)) return (int)Math.Round(img.Rect.W * 1.5);   // 2:3 placeholder, as the POC did
        using var s = Surface.Load(path);
        return (int)Math.Round((double)img.Rect.W * s.Height / s.Width);
    }
}
```

`Surface.Load` for `auto` cells opens the cover once per resolve; Phase 4's image cache
records dimensions alongside downloads so this becomes a lookup. Acceptable in Phase 1
because covers change every ten minutes, not every tick, and content keys skip the render
anyway.

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test --filter FullyQualifiedName~Resolve`
Expected: 4 passing.

- [ ] **Step 5: Commit**

```bash
git add src/DeskWall.Core/Resolve tests/DeskWall.Core.Tests/Resolve
git commit -m "Resolve: layout + value tree -> resolved components with repeater expansion and content keys"
```

---

### Task 10: `TickRunner`, `TickTimings`, `deskwall tick --measure`, first real layout

**Files:**
- Create: `src/DeskWall.Core/Tick/TickRunner.cs`, `src/DeskWall.Core/Tick/TickTimings.cs`,
  `src/DeskWall.Core/Tick/FrameState.cs`, `layouts/clock-disks.json`
- Modify: `src/DeskWall.Daemon/Program.cs`, `src/DeskWall.Daemon/NativeMethods.txt` (new: `AttachConsole`, `ATTACH_PARENT_PROCESS`)
- Test: `tests/DeskWall.Core.Tests/Tick/TickRunnerTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `TickTimings` with `long ResolveMs, DrawMs, EncodeMs, ApplyMs, ShortcutsMs, TotalMs, CpuMs; bool Skipped; int Redrawn; string ToTable()`.
  - `FrameState(Dictionary<string,string> KeysById, string SignatureKey, string FramePath)` persisted as `frame-state.json` in the runtime dir. `Load()`, `Save()`.
  - `TickRunner(LayoutFile layout, IReadOnlyList<ISource> sources, SourceRegistry registry, IClock clock, MonitorInfo monitor)` with `Task<TickTimings> RunAsync(bool force, bool apply, CancellationToken ct)`. Refreshes due sources (`NextDue <= now`), resolves, compares keys to `FrameState`, renders (full in this task; Task 11 makes it incremental), encodes to `Paths.InRuntime("deskwall.jpg")`, applies to `monitor.WallpaperMonitorId`, saves state. `Shortcuts` stage is a no-op returning the resolved shortcuts until Phase 3, timed anyway.

- [ ] **Step 1: Failing test**

```csharp
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using Xunit;

file sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

public class TickRunnerTests
{
    [Fact]
    public async Task Second_Tick_Without_Changes_Is_Skipped_And_Clock_Change_Redraws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests"); Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "tickbase.png");
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 30, 30, 30)); b.SavePng(basePng); }
        var layout = LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}}, "sources": [ { "name": "time", "type": "time" } ],
          "components": [ { "type": "text", "id": "clock", "rect": [10, 10, 200, 60], "text": { "bind": "time.now | HH:mm" }, "size": 40 } ] }
        """);
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
        var registry = new SourceRegistry();
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var monitor = new MonitorInfo(new DisplaySignature("TEST", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST");
        var runner = new TickRunner(layout, sources, registry, clock, monitor, statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"));

        var t1 = await runner.RunAsync(force: true, apply: false, default);
        Assert.False(t1.Skipped); Assert.Equal(1, t1.Redrawn);

        clock.Now = clock.Now.AddSeconds(10);            // same minute: time source not due
        var t2 = await runner.RunAsync(force: false, apply: false, default);
        Assert.True(t2.Skipped); Assert.Equal(0, t2.Redrawn); Assert.Equal(0, t2.EncodeMs);

        clock.Now = clock.Now.AddMinutes(1);              // next minute: due, key changes
        var t3 = await runner.RunAsync(force: false, apply: false, default);
        Assert.False(t3.Skipped); Assert.Equal(1, t3.Redrawn);
        Assert.Contains("resolve", t3.ToTable());
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter FullyQualifiedName~TickRunner`

- [ ] **Step 3: Implement**

`src/DeskWall.Core/Tick/TickTimings.cs`:

```csharp
namespace DeskWall.Core.Tick;

public sealed class TickTimings
{
    public long ResolveMs, DrawMs, EncodeMs, ApplyMs, ShortcutsMs, TotalMs;
    public double CpuMs;
    public bool Skipped;
    public int Redrawn;
    public string Source = "";

    public string ToTable() =>
        $"stage      ms\n" +
        $"resolve    {ResolveMs}\n" +
        $"draw       {DrawMs}\n" +
        $"encode     {EncodeMs}\n" +
        $"apply      {ApplyMs}\n" +
        $"shortcuts  {ShortcutsMs}\n" +
        $"total      {TotalMs}   cpu {CpuMs:N0}   redrawn {Redrawn}{(Skipped ? "   SKIPPED" : "")}";
}
```

`src/DeskWall.Core/Tick/FrameState.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Tick;

/// <summary>What the last rendered frame contained, so the next tick knows what changed.</summary>
public sealed class FrameState
{
    public Dictionary<string, string> KeysById { get; set; } = new();
    public string SignatureKey { get; set; } = "";
    public string FramePath { get; set; } = "";

    public static FrameState Load(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), FrameStateJsonContext.Default.FrameState) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, FrameStateJsonContext.Default.FrameState));
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSerializable(typeof(FrameState))]
internal partial class FrameStateJsonContext : JsonSerializerContext;
```

`src/DeskWall.Core/Tick/TickRunner.cs`:

```csharp
using System.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Sources;
using DeskWall.Core.Wallpaper;

namespace DeskWall.Core.Tick;

public sealed class TickRunner(
    LayoutFile layout,
    IReadOnlyList<ISource> sources,
    SourceRegistry registry,
    IClock clock,
    MonitorInfo monitor,
    string? statePath = null,
    string? outPath = null)
{
    private readonly string _statePath = statePath ?? Paths.InRuntime("frame-state.json");
    private readonly string _outPath = outPath ?? Paths.InRuntime(layout.Encode == "png" ? "deskwall.png" : "deskwall.jpg");
    private readonly string _framePath = Paths.InRuntime("frame.raw");

    /// <summary>Resolved shortcuts from the last run; Phase 3's manager consumes them.</summary>
    public IReadOnlyList<ResolvedShortcut> LastShortcuts { get; private set; } = [];

    public async Task<TickTimings> RunAsync(bool force, bool apply, CancellationToken ct)
    {
        var t = new TickTimings();
        var sw = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var now = clock.Now;

        // 1. refresh due sources
        foreach (var s in sources)
        {
            var snap = registry.Get(s.Name);
            if (!force && s.NextDue(snap.LastRefresh, now) > now) continue;
            try { registry.Set(snap.Succeeded(await s.RefreshAsync(ct), now)); }
            catch (Exception ex) { registry.Set(snap.Failed(ex.Message)); }
        }

        // 2. resolve + diff
        var canvas = monitor.Bounds with { X = 0, Y = 0 };
        var resolved = LayoutResolver.Resolve(layout, registry.Tree(), canvas);
        LastShortcuts = resolved.OfType<ResolvedShortcut>().ToList();
        var state = FrameState.Load(_statePath);
        var changed = resolved.Where(c => force || !state.KeysById.TryGetValue(c.Id, out var k) || k != c.ContentKey).ToList();
        var removed = state.KeysById.Keys.Except(resolved.Select(c => c.Id)).Any();
        var sameSig = state.SignatureKey == monitor.Signature.Key && File.Exists(_framePath) && File.Exists(_outPath);
        t.ResolveMs = sw.ElapsedMilliseconds;

        if (!force && changed.Count == 0 && !removed && sameSig)
        {
            t.Skipped = true;
            t.TotalMs = sw.ElapsedMilliseconds;
            t.CpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            return t;
        }

        // 3. draw (full render in this task; Task 11 makes it incremental)
        var d0 = sw.ElapsedMilliseconds;
        var baseRaw = BaseCache.Ensure(layout.BaseImage, canvas.W, canvas.H, layout.BaseFit);
        using var frame = new FrameRenderer(canvas.W, canvas.H).RenderAll(baseRaw, resolved);
        t.Redrawn = resolved.Count(c => c is not ResolvedShortcut);
        frame.SaveRaw(_framePath);
        t.DrawMs = sw.ElapsedMilliseconds - d0;

        // 4. encode
        var e0 = sw.ElapsedMilliseconds;
        if (layout.Encode == "png") frame.SavePng(_outPath); else frame.SaveJpeg(_outPath, layout.JpegQuality);
        t.EncodeMs = sw.ElapsedMilliseconds - e0;

        // 5. apply
        var a0 = sw.ElapsedMilliseconds;
        if (apply) WallpaperSetter.Set(monitor.WallpaperMonitorId, _outPath);
        t.ApplyMs = sw.ElapsedMilliseconds - a0;

        // 6. shortcuts: Phase 3. Timed so the table shape is final now.
        var s0 = sw.ElapsedMilliseconds;
        t.ShortcutsMs = sw.ElapsedMilliseconds - s0;

        // 7. state
        state.KeysById = resolved.ToDictionary(c => c.Id, c => c.ContentKey);
        state.SignatureKey = monitor.Signature.Key;
        state.FramePath = _framePath;
        state.Save(_statePath);

        t.TotalMs = sw.ElapsedMilliseconds;
        t.CpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        return t;
    }
}
```

`src/DeskWall.Daemon/NativeMethods.txt` (new file; the Daemon project also needs the CsWin32
package reference and a `NativeMethods.json` identical to Core's):

```
AttachConsole
FreeConsole
```

`src/DeskWall.Daemon/Program.cs`:

```csharp
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using DeskWall.Core.Wallpaper;
using Windows.Win32;

namespace DeskWall.Daemon;

internal static class Program
{
    private static int Main(string[] argv)
    {
        var cmd = argv.Length == 0 ? "run" : argv[0];
        var opts = argv.Skip(1).ToList();
        switch (cmd)
        {
            case "tick":
                PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);   // WinExe: borrow the caller's console
                return Tick(opts).GetAwaiter().GetResult();
            case "paths":
                PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);
                Console.WriteLine(Paths.RuntimeDir);
                return 0;
            default:
                PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);
                Console.Error.WriteLine($"deskwall: unknown or not yet implemented command '{cmd}'");
                return 2;
        }
    }

    /// <summary>deskwall tick [--layout path] [--force] [--measure] [--no-apply]</summary>
    private static async Task<int> Tick(List<string> opts)
    {
        var layoutPath = OptValue(opts, "--layout") ?? Paths.InRuntime("layout.json");
        if (!File.Exists(layoutPath)) { Console.Error.WriteLine($"no layout at {layoutPath}"); return 3; }
        var layout = LayoutFile.Load(layoutPath);
        var monitor = Monitors.Enumerate().First(m => m.IsPrimary);
        var clock = SystemClock.Instance;
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var registry = new SourceRegistry();
        WallpaperSetter.RecordRestorePoint();
        var runner = new TickRunner(layout, sources, registry, clock, monitor);
        var t = await runner.RunAsync(force: opts.Contains("--force"), apply: !opts.Contains("--no-apply"), CancellationToken.None);
        if (opts.Contains("--measure")) Console.WriteLine(t.ToTable());
        else Console.WriteLine($"{DateTime.Now:HH:mm:ss} total={t.TotalMs} ms cpu={t.CpuMs:N0} ms redrawn={t.Redrawn}{(t.Skipped ? " skipped" : "")}");
        return 0;
    }

    private static string? OptValue(List<string> opts, string name)
    {
        var i = opts.IndexOf(name);
        return i >= 0 && i + 1 < opts.Count ? opts[i + 1] : null;
    }
}
```

`layouts/clock-disks.json` (the POC's column, less the games; base image path is the
Spotlight asset the POC used):

```json
{
  "version": 1,
  "baseImage": "C:\\Windows\\SystemApps\\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\\DesktopSpotlight\\Assets\\Images\\image_3.jpg",
  "baseFit": "cover",
  "encode": "jpeg",
  "jpegQuality": 92,
  "sources": [
    { "name": "time", "type": "time" },
    { "name": "disks", "type": "disks", "every": 300 }
  ],
  "components": [
    { "type": "text", "id": "clock", "rect": [3220, 40, 172, 78], "z": 1,
      "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "weight": 300, "align": "right" },
    { "type": "repeater", "id": "drives", "rect": [3220, 1260, 172, 92], "z": 1,
      "items": { "bind": "disks.drives" }, "axis": "vertical", "gap": 0, "cellHeight": 46,
      "template": [
        { "type": "text", "id": "letter", "rect": [0, 0, 60, 24], "text": { "bind": "letter | \"{0}:\"" }, "size": 15 },
        { "type": "text", "id": "free", "rect": [60, 0, 112, 24], "text": { "bind": "freeGB | \"{0:N0} GB free\"" }, "size": 15, "align": "right" },
        { "type": "bar", "id": "bar", "rect": [0, 26, 172, 6], "fraction": { "bind": "usedFraction" }, "threshold": 0.85, "thresholdFill": "#FFD13438" }
      ] }
  ]
}
```

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test`
Expected: all green (about 30 tests).

- [ ] **Step 5: Run it for real, measure, record**

The POC task is still ticking every minute; pause it so the two do not fight:

```powershell
Disable-ScheduledTask -TaskName 'DeskWall Tick'
dotnet publish src/DeskWall.Daemon -c Release -r win-x64
Copy-Item layouts\clock-disks.json "$env:LOCALAPPDATA\DeskWall\layout.json"
$exe = "src\DeskWall.Daemon\bin\Release\net10.0-windows\win-x64\publish\deskwall.exe"
& $exe tick --force --measure
& $exe tick --measure          # expect SKIPPED within the same minute
Start-Sleep 61
& $exe tick --measure          # clock change: redraw
Enable-ScheduledTask -TaskName 'DeskWall Tick'
```

Expected: the desktop shows the clock and drive rows in the right column over the photo.
Paste the three tables into `docs/superpowers/plans/2026-09-20-phase1-spike-results.md`
under a heading `## Task 10 measurements`. The clock-change table is the number that
matters against the 60 ms wall / 40 ms CPU budget. If it misses, Task 11 is mandatory; if it
passes, Task 11 is still done, since spec section 6 requires it, but it must not regress this.

- [ ] **Step 6: Commit**

```bash
git add src tests layouts docs
git commit -m "tick: TickRunner with per-stage timings, frame state, first real layout"
```

---

### Task 11: Incremental redraw

**Files:**
- Modify: `src/DeskWall.Core/Tick/TickRunner.cs` (draw stage), `src/DeskWall.Core/Render/FrameRenderer.cs`
- Test: `tests/DeskWall.Core.Tests/Render/FrameRendererTests.cs` (add), `tests/DeskWall.Core.Tests/Tick/TickRunnerTests.cs` (add)

**Interfaces:**
- Produces: `FrameRenderer.RenderIncremental(Surface previous, string baseRawPath, IReadOnlyList<Resolved> all, IReadOnlySet<string> changedIds) : Surface`.

Algorithm: dirty = union of rects of changed components plus rects of components in the
previous frame that no longer exist (their rects come from `FrameState`, so `FrameState`
gains `RectsById`). Start from `previous`. For each dirty rect, copy that rect from the base
cache (restores the photo). Then draw, in z-order, every component whose rect intersects any
dirty rect. Components fully outside every dirty rect are untouched pixels from `previous`.

- [ ] **Step 1: Failing tests**

Add to `FrameRendererTests`:

```csharp
[Fact]
public void RenderIncremental_Only_Touches_Dirty_Rects()
{
    var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests"); Directory.CreateDirectory(dir);
    var basePng = Path.Combine(dir, "base2.png");
    using (var b = Surface.Create(40, 20)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(basePng); }
    var raw = BaseCache.Ensure(basePng, 40, 20, Fit.Cover);
    var r = new FrameRenderer(40, 20);
    Resolved left = new ResolvedBar("l", new Rect(0, 0, 20, 20), 1, 1, Color.White, new Color(255, 255, 0, 0), Axis.Horizontal);
    Resolved right = new ResolvedBar("r", new Rect(20, 0, 20, 20), 1, 1, Color.White, new Color(255, 0, 255, 0), Axis.Horizontal);
    using var first = r.RenderAll(raw, [left, right]);
    // change only the right one to blue-ish; the left must stay red without being redrawn
    Resolved right2 = new ResolvedBar("r", new Rect(20, 0, 20, 20), 1, 1, Color.White, new Color(255, 0, 0, 200), Axis.Horizontal);
    using var second = r.RenderIncremental(first, raw, [left, right2], new HashSet<string> { "r" }, previousRects: new Dictionary<string, Rect>());
    Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)0), second.GetPixel(5, 5));
    Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)200), second.GetPixel(30, 5));
}

[Fact]
public void RenderIncremental_Restores_Base_Where_A_Component_Vanished()
{
    var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests"); Directory.CreateDirectory(dir);
    var basePng = Path.Combine(dir, "base3.png");
    using (var b = Surface.Create(40, 20)) { b.Clear(new Color(255, 0, 0, 255)); b.SavePng(basePng); }
    var raw = BaseCache.Ensure(basePng, 40, 20, Fit.Cover);
    var r = new FrameRenderer(40, 20);
    Resolved gone = new ResolvedBar("g", new Rect(0, 0, 40, 20), 1, 1, Color.White, new Color(255, 255, 0, 0), Axis.Horizontal);
    using var first = r.RenderAll(raw, [gone]);
    using var second = r.RenderIncremental(first, raw, [], new HashSet<string>(), previousRects: new Dictionary<string, Rect> { ["g"] = gone.Rect });
    Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), second.GetPixel(5, 5));
}
```

Add to `TickRunnerTests`, at the end of the existing test:

```csharp
Assert.Equal(1, t3.Redrawn);   // already asserted; now also assert the frame stage was cheaper than a full render
Assert.True(t3.DrawMs <= t1.DrawMs);
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test --filter "FullyQualifiedName~FrameRenderer|FullyQualifiedName~TickRunner"`

- [ ] **Step 3: Implement**

Add to `FrameRenderer`:

```csharp
public Surface RenderIncremental(Surface previous, string baseRawPath, IReadOnlyList<Resolved> all,
    IReadOnlySet<string> changedIds, IReadOnlyDictionary<string, Rect> previousRects)
{
    var dirty = new List<Rect>();
    foreach (var c in all) if (changedIds.Contains(c.Id)) dirty.Add(c.Rect);
    var liveIds = all.Select(c => c.Id).ToHashSet();
    foreach (var (id, rect) in previousRects) if (!liveIds.Contains(id)) dirty.Add(rect);
    if (dirty.Count == 0) return previous;

    var frame = Surface.Create(width, height);
    frame.CopyRect(previous, new Rect(0, 0, width, height));
    using var baseSurf = Surface.LoadRaw(baseRawPath);
    foreach (var d in dirty) frame.CopyRect(baseSurf, Clip(d));
    foreach (var c in all.OrderBy(c => c.Z))
        if (dirty.Any(d => d.Intersects(c.Rect))) Draw(frame, c);
    return frame;
}

private Rect Clip(Rect r)
{
    var x = Math.Max(0, r.X); var y = Math.Max(0, r.Y);
    return new Rect(x, y, Math.Min(width, r.Right) - x, Math.Min(height, r.Bottom) - y);
}
```

Note: a component that intersects a dirty rect is redrawn whole, which may spill outside the
dirty rect over pixels that were correct; since it is redrawn from the same resolved state
those pixels come out identical. Correct, if not minimal.

In `FrameState` add `public Dictionary<string, Rect> RectsById { get; set; } = new();` with
`[JsonConverter(typeof(RectConverter))]` on the value type via a small `RectDictConverter`,
or store rects as `int[]` arrays: `Dictionary<string, int[]>` is simplest for the
source-generated context. Use `int[]`.

In `TickRunner.RunAsync` replace step 3 with:

```csharp
var d0 = sw.ElapsedMilliseconds;
var baseRaw = BaseCache.Ensure(layout.BaseImage, canvas.W, canvas.H, layout.BaseFit);
Surface frame;
if (force || !sameSig)
{
    frame = new FrameRenderer(canvas.W, canvas.H).RenderAll(baseRaw, resolved);
    t.Redrawn = resolved.Count(c => c is not ResolvedShortcut);
}
else
{
    using var previous = Surface.LoadRaw(_framePath);
    var changedIds = changed.Select(c => c.Id).ToHashSet();
    var prevRects = state.RectsById.ToDictionary(kv => kv.Key, kv => new Rect(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
    frame = new FrameRenderer(canvas.W, canvas.H).RenderIncremental(previous, baseRaw, resolved, changedIds, prevRects);
    t.Redrawn = changedIds.Count;
}
using (frame)
{
    frame.SaveRaw(_framePath);
    t.DrawMs = sw.ElapsedMilliseconds - d0;
    // encode, apply, as before
    ...
}
state.RectsById = resolved.ToDictionary(c => c.Id, c => new[] { c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H });
```

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 5: Measure again and record**

Repeat Task 10 Step 5's three-command sequence. Append the tables under
`## Task 11 measurements` in the spike results file. The clock-change tick must be at or
under the Task 10 number for `draw`, and the whole tick under 60 ms wall / 40 ms CPU. If
`encode` alone exceeds the budget, record it: that is the input to the Phase 2 decision on
whether to encode at lower quality or accept a wider budget, and it is the owner's call.

- [ ] **Step 6: Commit and close the phase**

```bash
git add src tests docs
git commit -m "Render: incremental redraw of dirty rects from the previous frame"
```

Phase 1 exit criteria, all ticked before Phase 2 through 4 plans are written:

- [ ] `dotnet test` green on `v1`.
- [ ] `dotnet publish` of the daemon: zero IL warnings, `deskwall.exe` under 12 MB on disk.
- [ ] `deskwall tick --measure` tables for force, skip and clock-change recorded.
- [ ] Desktop shows `layouts/clock-disks.json` correctly in the right column over the photo.
- [ ] POC scheduled task re-enabled (it still owns the covers and shortcuts until Phase 3).

## Self-review notes

- Spec 4.1 `time` source: covered (Task 5). `disks`: covered. Others: Phase 4.
- Spec 4.2 grammar: Task 3, including `[key]` lookup and quoted formats.
- Spec 4.3 all five components: defs in Task 4, resolution in Task 6, drawing in Task 8;
  shortcut draws nothing, consumed in Phase 3 via `TickRunner.LastShortcuts`.
- Spec 4.4 content keys and skip: Tasks 6 and 10.
- Spec 5 signature and closest-layout: signature and similarity in Task 7; the layout store
  and scaling live in Phase 2 with hot reload, since they only matter once something runs
  continuously.
- Spec 6 pipeline: steps 1 to 6 and 8 across Tasks 8 to 11; step 7 is Phase 3.
- Type names: `Rect`, `Fit`, `Axis`, `Align` in `DeskWall.Core`; `Color`, `TextStyle`,
  `TextEffect` in `DeskWall.Core.Render`; `Binding` type sits in namespace
  `DeskWall.Core.Binding`, which is why `PropertyValue` writes `Binding.Binding`.
