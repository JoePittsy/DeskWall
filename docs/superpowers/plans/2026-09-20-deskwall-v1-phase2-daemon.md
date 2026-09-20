# DeskWall v1 Phase 2: Resident Daemon Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `deskwall run` stays resident with a hidden window and a tray icon, wakes only when a
source is due or the display, session or layout changes, runs the Phase 1 tick, trims its
working set, and reports its own footprint in the tray tooltip. `install`/`uninstall` manage
the Run key and the wallpaper restore point.

**Architecture:** Core gains a `Scheduler` (pure next-wake maths), a `LayoutStore` (display
signature to layout file, with proportional scaling for unknown signatures), a `RollingLog`, and
`Startup` (Run key). The Daemon gains a `HostWindow` (hidden top-level window and message pump
that turns Win32 messages into `WakeReason`s), a `TrayIcon`, and `DaemonLoop` that owns the
`TickRunner` and sleeps on `MsgWaitForMultipleObjectsEx` with a waitable timer.

**Tech Stack:** as Phase 1: .NET 10, native AOT, CsWin32 (`allowMarshaling: false`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md` (sections 3, 5, 1.2)
**Master plan:** `docs/superpowers/plans/2026-09-20-deskwall-v1-master.md`
**Phase 1 results:** `docs/superpowers/plans/2026-09-20-phase1-spike-results.md` (CsWin32 shapes; read before any interop)

## Global Constraints

- Everything in the master plan's Global Constraints. In particular the budget (spec 1.2): idle
  private working set 10 MB after trim; idle CPU 0; clock-only tick 60 ms wall / 40 ms CPU;
  cold start to first wallpaper 500 ms; under 100 handles and under 5 threads at idle.
- **The previous frame is never held in memory between ticks** (19.8 MB at 3440x1440). It is
  loaded from `frame.raw` per tick (Phase 1 results, Task 11 note).
- **No polling loop.** The daemon blocks in one `MsgWaitForMultipleObjectsEx` call until the
  timer or a message. A `while(true) Sleep(1000)` anywhere is a defect.
- **No thread per source.** Source refreshes are awaited on the tick with a per-source timeout;
  a refresh that overruns posts a wake when it finishes (Phase 4 relies on this).
- CsWin32 facts from the Phase 1 results file apply: COM methods are void and throw; static
  entry points return `HRESULT`; `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]`
  for callbacks; `Com.EnsureInitialized()` before any COM.
- ASCII-only sources. Tests run under `DESKWALL_HOME` (set by the test assembly initializer);
  a test that writes to the real runtime dir is a defect.
- Lane assignment (agent cap stated in the ledger before dispatch): `lane/p2-plumbing`
  (Sonnet: Tasks 2, 3, 4, 5), `lane/p2-host` (Opus: Tasks 6, 7). Task 1 (seam) and Tasks 8, 9
  (integration, measurement) by the controller. Reviews by the controller.

## Interfaces shipped by Phase 1 that this phase consumes

```csharp
// DeskWall.Core
Paths.RuntimeDir; Paths.InRuntime(params string[])            // honours DESKWALL_HOME
Rect(int X,int Y,int W,int H) { Right, Bottom, Intersects, Offset, Scale(sx,sy) }
Com.EnsureInitialized()
// Sources
interface ISource { string Name; DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now); ValueTask<RecordValue> RefreshAsync(CancellationToken) }
interface IClock { DateTimeOffset Now }  SystemClock.Instance
SourceFactory.Create(SourceDef, IClock) : ISource
SourceRegistry { Get(name): SourceSnapshot; Set(snapshot); Tree(): RecordValue; All }
SourceSnapshot(Name, Values, LastRefresh, LastError, ConsecutiveFailures) { Succeeded(v, at); Failed(err) }
// Layout
LayoutFile { Version, BaseImage, BaseFit, Encode, JpegQuality, Sources, Components; static Parse(json), Load(path); ToJson(); Save(path) }
ComponentDef { Id, Rect, Z } : TextDef | ImageDef | BarDef | ShortcutDef | RepeaterDef { Template }
// Display
DisplaySignature(DevicePath, Width, Height, ScalePercent) { Key; Aspect; static Parse(key); Similarity(other): int }
MonitorInfo(Signature, Bounds, IsPrimary, WallpaperMonitorId);  Monitors.Enumerate()
// Tick
TickRunner(layout, sources, registry, clock, monitor, statePath?, outPath?, framePath?) { Task<TickTimings> RunAsync(force, apply, ct); LastShortcuts }
TickTimings { ResolveMs, DrawMs, EncodeMs, ApplyMs, ShortcutsMs, TotalMs, CpuMs, Skipped, Redrawn; ToTable() }
WallpaperSetter.Set(monitorId, path); Get(monitorId); RecordRestorePoint(); Restore()
```

## File structure

```
src/DeskWall.Core/Scheduling/Scheduler.cs        next-wake maths over ISource list (Task 2)
src/DeskWall.Core/Scheduling/WakeReason.cs       enum + record (Task 1)
src/DeskWall.Core/Layout/LayoutStore.cs          signature -> layout path; closest + scale (Task 3)
src/DeskWall.Core/Layout/LayoutScaler.cs         proportional scaling of a LayoutFile (Task 3)
src/DeskWall.Core/Diagnostics/RollingLog.cs      (Task 4)
src/DeskWall.Core/Diagnostics/Footprint.cs       working set / cpu / handles / threads (Task 1)
src/DeskWall.Core/Startup.cs                     Run key install/uninstall (Task 5)
src/DeskWall.Daemon/Host/HostWindow.cs           hidden window, message pump, WakeReason events (Task 6)
src/DeskWall.Daemon/Host/WaitableTimer.cs        (Task 6)
src/DeskWall.Daemon/Host/TrayIcon.cs             (Task 7)
src/DeskWall.Daemon/DaemonLoop.cs                owns TickRunner; the run loop (Task 8)
src/DeskWall.Daemon/Program.cs                   run / install / uninstall wiring (Task 8)
src/DeskWall.Daemon/NativeMethods.txt            grows in Tasks 6, 7, 8
tests/DeskWall.Core.Tests/Scheduling/SchedulerTests.cs
tests/DeskWall.Core.Tests/Layout/LayoutStoreTests.cs
tests/DeskWall.Core.Tests/Layout/LayoutScalerTests.cs
tests/DeskWall.Core.Tests/Diagnostics/RollingLogTests.cs
tests/DeskWall.Core.Tests/StartupTests.cs
```

---

### Task 1: Seam: `WakeReason`, `Footprint`, `IDaemonHost` contract (controller)

**Files:**
- Create: `src/DeskWall.Core/Scheduling/WakeReason.cs`, `src/DeskWall.Core/Diagnostics/Footprint.cs`
- Modify: `src/DeskWall.Core/NativeMethods.txt` (append `GetProcessMemoryInfo`, `PROCESS_MEMORY_COUNTERS_EX`, `SetProcessWorkingSetSize`, `GetProcessHandleCount`, `GetCurrentProcess`)
- Test: `tests/DeskWall.Core.Tests/Diagnostics/FootprintTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace DeskWall.Core.Scheduling;
public enum WakeKind { Timer, DisplayChange, SessionUnlock, LayoutChanged, Manual, SourceCompleted, Shutdown }
public readonly record struct WakeReason(WakeKind Kind, string? Detail = null);

namespace DeskWall.Core.Diagnostics;
public sealed record Footprint(long PrivateBytes, long WorkingSetBytes, TimeSpan TotalCpu, int Handles, int Threads)
{
    public static Footprint Current();          // GetProcessMemoryInfo(PrivateUsage, WorkingSetSize), Process.TotalProcessorTime, GetProcessHandleCount, Process.Threads.Count
    public static void Trim();                  // SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1)
    public string Short();                      // "4.8 MB . cpu 1.2 s . 41 h . 3 t"
}
```

- [ ] **Step 1: Failing test**

```csharp
using DeskWall.Core.Diagnostics;
using Xunit;

public class FootprintTests
{
    [Fact]
    public void Current_Reports_Plausible_Numbers()
    {
        var f = Footprint.Current();
        Assert.InRange(f.WorkingSetBytes, 1_000_000, 2_000_000_000);
        Assert.InRange(f.PrivateBytes, 1_000_000, 2_000_000_000);
        Assert.InRange(f.Handles, 10, 100_000);
        Assert.InRange(f.Threads, 1, 1000);
        Assert.Contains("MB", f.Short());
    }

    [Fact]
    public void Trim_Does_Not_Throw_And_Working_Set_Does_Not_Grow()
    {
        var before = Footprint.Current().WorkingSetBytes;
        Footprint.Trim();
        var after = Footprint.Current().WorkingSetBytes;
        Assert.True(after <= before + 512 * 1024);
    }
}
```

- [ ] **Step 2: Run, expect compile failure.** `dotnet test --filter FullyQualifiedName~Footprint`

- [ ] **Step 3: Implement**

```csharp
namespace DeskWall.Core.Scheduling;

public enum WakeKind { Timer, DisplayChange, SessionUnlock, LayoutChanged, Manual, SourceCompleted, Shutdown }

/// <summary>Why the daemon woke. Detail is free text for the log (e.g. the changed file).</summary>
public readonly record struct WakeReason(WakeKind Kind, string? Detail = null)
{
    public override string ToString() => Detail is null ? Kind.ToString() : $"{Kind} ({Detail})";
}
```

```csharp
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.ProcessStatus;

namespace DeskWall.Core.Diagnostics;

public sealed unsafe record Footprint(long PrivateBytes, long WorkingSetBytes, TimeSpan TotalCpu, int Handles, int Threads)
{
    public static Footprint Current()
    {
        var h = PInvoke.GetCurrentProcess();
        var pmc = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX) };
        PInvoke.GetProcessMemoryInfo(h, (PROCESS_MEMORY_COUNTERS*)&pmc, pmc.cb);
        uint handles; PInvoke.GetProcessHandleCount(h, &handles);
        using var p = Process.GetCurrentProcess();
        return new Footprint((long)pmc.PrivateUsage, (long)pmc.WorkingSetSize, p.TotalProcessorTime, (int)handles, p.Threads.Count);
    }

    /// <summary>Give freed pages back so Task Manager shows the idle number, not the render peak.</summary>
    public static void Trim() => PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);

    public string Short() => $"{WorkingSetBytes / 1048576.0:0.0} MB . cpu {TotalCpu.TotalSeconds:0.0} s . {Handles} h . {Threads} t";
}
```

`GetCurrentProcess` returns a pseudo-handle; with `useSafeHandles: true` CsWin32 may hand back a
`SafeHandle` wrapper; either is fine to pass straight back in. Adjust to what the generator emits
and record in the report. `SetProcessWorkingSetSize` with `(SIZE_T)-1` for both is the documented
"trim now" call.

- [ ] **Step 4: Run tests, expect pass.** Also `dotnet build` zero warnings.

- [ ] **Step 5: Commit** `git commit -m "Phase 2 seam: WakeReason, Footprint (memory/cpu/handles/threads + trim)"`

Fan-out starts here.

---

### Task 2: `Scheduler` (lane `lane/p2-plumbing`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Scheduling/Scheduler.cs`
- Test: `tests/DeskWall.Core.Tests/Scheduling/SchedulerTests.cs`

**Interfaces:**
- Consumes: `ISource.NextDue`, `SourceRegistry.Get(name).LastRefresh`.
- Produces:

```csharp
public sealed class Scheduler(IReadOnlyList<ISource> sources, SourceRegistry registry)
{
    /// <summary>Earliest NextDue across all sources, clamped to [now + MinDelay, now + MaxDelay].</summary>
    public DateTimeOffset NextWake(DateTimeOffset now);
    /// <summary>Sources due at or before now.</summary>
    public IReadOnlyList<ISource> Due(DateTimeOffset now);
    public static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(250);   // never spin
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);         // always wake eventually (clock drift, sleep/resume)
}
```

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core.Scheduling;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedSource(string name, TimeSpan every) : PeriodicSource(name, every)
{
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(ValueTree.Empty);
}

public class SchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 14, 32, 5, TimeSpan.Zero);

    [Fact]
    public void Never_Run_Sources_Are_Due_Now_And_NextWake_Is_MinDelay()
    {
        var reg = new SourceRegistry();
        var s = new Scheduler([new FixedSource("a", TimeSpan.FromMinutes(5))], reg);
        Assert.Single(s.Due(T0));
        Assert.Equal(T0 + Scheduler.MinDelay, s.NextWake(T0));
    }

    [Fact]
    public void NextWake_Is_Earliest_Due()
    {
        var reg = new SourceRegistry();
        reg.Set(SourceSnapshot.Initial("a").Succeeded(ValueTree.Empty, T0));
        reg.Set(SourceSnapshot.Initial("b").Succeeded(ValueTree.Empty, T0));
        var s = new Scheduler([new FixedSource("a", TimeSpan.FromMinutes(5)), new FixedSource("b", TimeSpan.FromSeconds(90))], reg);
        Assert.Equal(T0.AddSeconds(90), s.NextWake(T0));
        Assert.Empty(s.Due(T0.AddSeconds(89)));
        Assert.Equal("b", Assert.Single(s.Due(T0.AddSeconds(90))).Name);
    }

    [Fact]
    public void NextWake_Is_Clamped_To_MaxDelay_And_Empty_List()
    {
        var reg = new SourceRegistry();
        reg.Set(SourceSnapshot.Initial("a").Succeeded(ValueTree.Empty, T0));
        var s = new Scheduler([new FixedSource("a", TimeSpan.FromHours(3))], reg);
        Assert.Equal(T0 + Scheduler.MaxDelay, s.NextWake(T0));
        Assert.Equal(T0 + Scheduler.MaxDelay, new Scheduler([], reg).NextWake(T0));
    }

    [Fact]
    public void Time_Source_Wakes_On_The_Minute()
    {
        var reg = new SourceRegistry();
        reg.Set(SourceSnapshot.Initial("time").Succeeded(ValueTree.Empty, T0));
        var s = new Scheduler([new TimeSource("time", SystemClock.Instance)], reg);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 14, 33, 0, TimeSpan.Zero), s.NextWake(T0));
    }
}
```

- [ ] **Step 2: Run, expect compile failure.** `dotnet test --filter FullyQualifiedName~Scheduler`

- [ ] **Step 3: Implement**

```csharp
using DeskWall.Core.Sources;

namespace DeskWall.Core.Scheduling;

/// <summary>Pure next-wake maths. No timers here; the host owns the timer.</summary>
public sealed class Scheduler(IReadOnlyList<ISource> sources, SourceRegistry registry)
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    public DateTimeOffset NextWake(DateTimeOffset now)
    {
        var earliest = now + MaxDelay;
        foreach (var s in sources)
        {
            var due = s.NextDue(registry.Get(s.Name).LastRefresh, now);
            if (due < earliest) earliest = due;
        }
        var floor = now + MinDelay;
        return earliest < floor ? floor : earliest;
    }

    public IReadOnlyList<ISource> Due(DateTimeOffset now)
        => sources.Where(s => s.NextDue(registry.Get(s.Name).LastRefresh, now) <= now).ToList();
}
```

- [ ] **Step 4: Run tests, expect pass** (4).
- [ ] **Step 5: Commit** `git commit -m "Scheduler: next-wake and due-set maths over sources"`

---

### Task 3: `LayoutStore` and `LayoutScaler` (lane `lane/p2-plumbing`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Layout/LayoutStore.cs`, `src/DeskWall.Core/Layout/LayoutScaler.cs`
- Test: `tests/DeskWall.Core.Tests/Layout/LayoutStoreTests.cs`, `tests/DeskWall.Core.Tests/Layout/LayoutScalerTests.cs`

**Interfaces:**
- Consumes: `DisplaySignature` (Key, Parse, Similarity), `LayoutFile`, `ComponentDef.Rect`, `RepeaterDef.Template`, `Rect.Scale`.
- Produces:

```csharp
/// <summary>layouts.json in the runtime dir: { "layouts": { "<signature key>": "<layout path>" } }.
/// Layout paths are absolute or relative to the runtime dir.</summary>
public sealed class LayoutStore
{
    public LayoutStore(string storePath);                     // default: Paths.InRuntime("layouts.json")
    public static LayoutStore Default();
    public IReadOnlyDictionary<string, string> Entries { get; }        // key -> resolved absolute path
    public void Set(DisplaySignature sig, string layoutPath);          // saves atomically
    public void Remove(DisplaySignature sig);
    /// <summary>Exact match, else the closest by Similarity (ties: most recently written file), scaled to sig. Null when the store is empty.</summary>
    public LayoutResolution? Resolve(DisplaySignature sig);
    /// <summary>Every file the daemon must watch: the store itself and all layout files.</summary>
    public IReadOnlyList<string> WatchPaths { get; }
}
public sealed record LayoutResolution(LayoutFile Layout, string SourcePath, DisplaySignature SourceSignature, bool Scaled);

public static class LayoutScaler
{
    /// <summary>Scale every rect from one canvas to another. Repeater templates scale too.
    /// Text sizes and gaps scale by the geometric mean of sx, sy. Widths of images keep their aspect
    /// (the repeater's auto cell height handles the rest).</summary>
    public static LayoutFile Scale(LayoutFile source, DisplaySignature from, DisplaySignature to);
}
```

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using Xunit;

public class LayoutScalerTests
{
    private const string Json = """
    { "version": 1, "baseImage": "x.jpg", "sources": [],
      "components": [
        { "type": "text", "id": "clock", "rect": [3220, 40, 172, 78], "size": 64, "text": "x" },
        { "type": "repeater", "id": "r", "rect": [3220, 1260, 172, 92], "gap": 14, "cellHeight": 46, "items": { "bind": "d.items" },
          "template": [ { "type": "bar", "id": "b", "rect": [0, 26, 172, 6], "fraction": 0.5 } ] }
      ] }
    """;

    [Fact]
    public void Scales_Rects_Templates_Sizes_And_Gaps()
    {
        var src = LayoutFile.Parse(Json);
        var from = new DisplaySignature("A", 3440, 1440, 100);
        var to = new DisplaySignature("B", 1720, 720, 100);   // exactly half
        var s = LayoutScaler.Scale(src, from, to);
        var clock = (TextDef)s.Components[0];
        Assert.Equal(new Rect(1610, 20, 86, 39), clock.Rect);
        Assert.Equal("32", clock.Size.LiteralText);
        var rep = (RepeaterDef)s.Components[1];
        Assert.Equal(new Rect(1610, 630, 86, 46), rep.Rect);
        Assert.Equal(7, rep.Gap);
        Assert.Equal("23", rep.CellHeight.LiteralText);
        Assert.Equal(new Rect(0, 13, 86, 3), rep.Template[0].Rect);
        Assert.NotSame(src, s);
        Assert.Equal(new Rect(3220, 40, 172, 78), ((TextDef)src.Components[0]).Rect);   // source untouched
    }

    [Fact]
    public void Bound_Size_And_Auto_CellHeight_Are_Left_Alone()
    {
        var src = LayoutFile.Parse("""{ "version": 1, "baseImage": "x", "sources": [], "components": [
          { "type": "text", "id": "t", "rect": [0,0,100,100], "text": "x", "size": { "bind": "a.b" } },
          { "type": "repeater", "id": "r", "rect": [0,0,100,100], "items": { "bind": "a.c" }, "template": [] } ] }""");
        var s = LayoutScaler.Scale(src, new DisplaySignature("A", 200, 200, 100), new DisplaySignature("B", 100, 100, 100));
        Assert.True(((TextDef)s.Components[0]).Size.IsBound);
        Assert.Equal("auto", ((RepeaterDef)s.Components[1]).CellHeight.LiteralText);
    }
}

public class LayoutStoreTests
{
    private static (LayoutStore store, string dir) Fresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (new LayoutStore(Path.Combine(dir, "layouts.json")), dir);
    }

    private static string WriteLayout(string dir, string name, int w) 
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, $$$"""{ "version": 1, "baseImage": "x.jpg", "sources": [], "components": [ { "type": "text", "id": "t", "rect": [{{{w - 100}}}, 0, 100, 50], "text": "x" } ] }""");
        return p;
    }

    [Fact]
    public void Empty_Store_Resolves_Null()
    {
        var (store, _) = Fresh();
        Assert.Null(store.Resolve(new DisplaySignature("A", 3440, 1440, 100)));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Exact_Match_Is_Not_Scaled_And_Persists()
    {
        var (store, dir) = Fresh();
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        var path = WriteLayout(dir, "a.json", 3440);
        store.Set(sig, path);
        var again = new LayoutStore(Path.Combine(dir, "layouts.json"));
        var r = again.Resolve(sig)!;
        Assert.False(r.Scaled);
        Assert.Equal(path, r.SourcePath);
        Assert.Equal(new Rect(3340, 0, 100, 50), r.Layout.Components[0].Rect);
        Assert.Contains(path, again.WatchPaths);
        Assert.Contains(Path.Combine(dir, "layouts.json"), again.WatchPaths);
    }

    [Fact]
    public void Unknown_Signature_Picks_Closest_And_Scales()
    {
        var (store, dir) = Fresh();
        var ultrawide = new DisplaySignature("A", 3440, 1440, 100);
        var laptop = new DisplaySignature("B", 1920, 1080, 100);
        store.Set(ultrawide, WriteLayout(dir, "uw.json", 3440));
        store.Set(laptop, WriteLayout(dir, "lap.json", 1920));
        // same device A at a new resolution: device match beats aspect match
        var r = store.Resolve(new DisplaySignature("A", 1720, 720, 100))!;
        Assert.True(r.Scaled);
        Assert.Equal(ultrawide, r.SourceSignature);
        Assert.Equal(new Rect(1670, 0, 50, 25), r.Layout.Components[0].Rect);
        // unknown device, 16:9: aspect match picks the laptop layout
        var r2 = store.Resolve(new DisplaySignature("C", 3840, 2160, 150))!;
        Assert.Equal(laptop, r2.SourceSignature);
        Assert.Equal(new Rect(3640, 0, 200, 100), r2.Layout.Components[0].Rect);
    }

    [Fact]
    public void Remove_And_Missing_File_Are_Handled()
    {
        var (store, dir) = Fresh();
        var sig = new DisplaySignature("A", 3440, 1440, 100);
        var path = WriteLayout(dir, "a.json", 3440);
        store.Set(sig, path);
        File.Delete(path);
        Assert.Null(store.Resolve(sig));   // entry exists but file is gone: treated as absent, not thrown
        store.Remove(sig);
        Assert.Empty(store.Entries);
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

`LayoutScaler.cs`:

```csharp
using System.Globalization;
using DeskWall.Core.Display;

namespace DeskWall.Core.Layout;

public static class LayoutScaler
{
    public static LayoutFile Scale(LayoutFile source, DisplaySignature from, DisplaySignature to)
    {
        var sx = (double)to.Width / from.Width;
        var sy = (double)to.Height / from.Height;
        var sm = Math.Sqrt(sx * sy);
        // Deep copy through JSON so the source is untouched; the source generator makes this AOT-safe.
        var copy = LayoutFile.Parse(source.ToJson());
        foreach (var c in copy.Components) ScaleComponent(c, sx, sy, sm);
        return copy;
    }

    private static void ScaleComponent(ComponentDef c, double sx, double sy, double sm)
    {
        c.Rect = c.Rect.Scale(sx, sy);
        switch (c)
        {
            case TextDef t:
                ScaleLiteral(t, nameof(TextDef.Size), sm);
                ScaleLiteral(t, nameof(TextDef.EffectRadius), sm);
                break;
            case ImageDef i:
                ScaleLiteral(i, nameof(ImageDef.Radius), sm);
                break;
            case RepeaterDef r:
                r.Gap = (int)Math.Round(r.Gap * sm);
                if (!r.CellHeight.IsBound && !string.Equals(r.CellHeight.LiteralText, "auto", StringComparison.OrdinalIgnoreCase))
                    r.CellHeight = ScaleNumber(r.CellHeight, r.Axis == Axis.Vertical ? sy : sx);
                foreach (var child in r.Template) ScaleComponent(child, sx, sy, sm);
                break;
        }
    }

    private static void ScaleLiteral(ComponentDef c, string property, double factor)
    {
        // Only the few numeric literals we know about; keep it explicit for AOT (no reflection).
        switch (c, property)
        {
            case (TextDef t, nameof(TextDef.Size)): t.Size = ScaleNumber(t.Size, factor); break;
            case (TextDef t, nameof(TextDef.EffectRadius)): t.EffectRadius = ScaleNumber(t.EffectRadius, factor); break;
            case (ImageDef i, nameof(ImageDef.Radius)): i.Radius = ScaleNumber(i.Radius, factor); break;
        }
    }

    private static PropertyValue ScaleNumber(PropertyValue p, double factor)
    {
        if (p.IsBound || !double.TryParse(p.LiteralText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return p;
        return PropertyValue.Literal(Math.Round(v * factor));
    }
}
```

`LayoutStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Display;

namespace DeskWall.Core.Layout;

public sealed record LayoutResolution(LayoutFile Layout, string SourcePath, DisplaySignature SourceSignature, bool Scaled);

public sealed class LayoutStore
{
    private readonly string _storePath;
    private readonly string _baseDir;
    private Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public LayoutStore(string storePath)
    {
        _storePath = Path.GetFullPath(storePath);
        _baseDir = Path.GetDirectoryName(_storePath)!;
        Load();
    }

    public static LayoutStore Default() => new(Paths.InRuntime("layouts.json"));

    public IReadOnlyDictionary<string, string> Entries => _entries.ToDictionary(kv => kv.Key, kv => Resolve(kv.Value), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> WatchPaths => [_storePath, .. _entries.Values.Select(Resolve)];

    public void Set(DisplaySignature sig, string layoutPath) { _entries[sig.Key] = Path.GetFullPath(layoutPath); Save(); }

    public void Remove(DisplaySignature sig) { if (_entries.Remove(sig.Key)) Save(); }

    public LayoutResolution? Resolve(DisplaySignature sig)
    {
        if (_entries.TryGetValue(sig.Key, out var exact) && File.Exists(Resolve(exact)))
            return new LayoutResolution(LayoutFile.Load(Resolve(exact)), Resolve(exact), sig, Scaled: false);

        LayoutResolution? best = null; var bestScore = -1; DateTime bestWrite = DateTime.MinValue;
        foreach (var (key, rel) in _entries)
        {
            var path = Resolve(rel);
            if (!File.Exists(path)) continue;
            DisplaySignature candidate;
            try { candidate = DisplaySignature.Parse(key); } catch (FormatException) { continue; }
            var score = sig.Similarity(candidate);
            var write = File.GetLastWriteTimeUtc(path);
            if (score > bestScore || (score == bestScore && write > bestWrite))
            {
                bestScore = score; bestWrite = write;
                best = new LayoutResolution(LayoutScaler.Scale(LayoutFile.Load(path), candidate, sig), path, candidate, Scaled: true);
            }
        }
        return best;
    }

    private string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(_baseDir, p));

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var doc = JsonSerializer.Deserialize(File.ReadAllText(_storePath), LayoutStoreJsonContext.Default.LayoutStoreFile);
            _entries = new(doc?.Layouts ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { _entries = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        Directory.CreateDirectory(_baseDir);
        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new LayoutStoreFile { Layouts = _entries }, LayoutStoreJsonContext.Default.LayoutStoreFile));
        File.Move(tmp, _storePath, overwrite: true);
    }
}

public sealed class LayoutStoreFile
{
    public Dictionary<string, string> Layouts { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LayoutStoreFile))]
internal partial class LayoutStoreJsonContext : JsonSerializerContext;
```

Note the scaler test expects `Rect.Scale` rounding: 3220*0.5 = 1610, 40*0.5 = 20, 172*0.5 = 86, 78*0.5 = 39; 1260*0.5 = 630, 92*0.5 = 46; gap 14*0.5 = 7; cellHeight 46*0.5 = 23; size 64*0.5 = 32; template rect [0,26,172,6] -> [0,13,86,3]. `Math.Round` uses banker's rounding on exact .5 values: none of these are .5 cases.

- [ ] **Step 4: Run tests, expect pass** (6). `dotnet build` zero warnings.
- [ ] **Step 5: Commit** `git commit -m "LayoutStore: signature -> layout map with closest-match scaling; LayoutScaler"`

---

### Task 4: `RollingLog` (lane `lane/p2-plumbing`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Diagnostics/RollingLog.cs`
- Test: `tests/DeskWall.Core.Tests/Diagnostics/RollingLogTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>Append-only text log in the runtime dir: deskwall.log, rolled to deskwall.1.log when it
/// passes MaxBytes. One line per entry: "yyyy-MM-dd HH:mm:ss.fff [LEVEL] message". Thread-safe.</summary>
public sealed class RollingLog(string path, long maxBytes = 1_000_000)
{
    public static RollingLog Default();          // Paths.InRuntime("deskwall.log")
    public void Info(string message);
    public void Warn(string message);
    public void Error(string message, Exception? ex = null);   // ex: type + message + first stack line
    public string? LastError { get; }            // most recent Error message this process, for the tray tooltip / designer
}
```

- [ ] **Step 1: Failing tests**

```csharp
using DeskWall.Core.Diagnostics;
using Xunit;

public class RollingLogTests
{
    private static string Temp(string name) { var d = Path.Combine(Path.GetTempPath(), "deskwall-tests", "log-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return Path.Combine(d, name); }

    [Fact]
    public void Writes_Lines_With_Level_And_Tracks_LastError()
    {
        var p = Temp("t.log");
        var log = new RollingLog(p);
        log.Info("hello");
        log.Error("boom", new InvalidOperationException("why"));
        var lines = File.ReadAllLines(p);
        Assert.Equal(2, lines.Length);
        Assert.Contains("[INFO] hello", lines[0]);
        Assert.Contains("[ERROR] boom: InvalidOperationException: why", lines[1]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", lines[0]);
        Assert.Equal("boom: InvalidOperationException: why", log.LastError);
    }

    [Fact]
    public void Rolls_When_Over_MaxBytes()
    {
        var p = Temp("r.log");
        var log = new RollingLog(p, maxBytes: 500);
        for (var i = 0; i < 20; i++) log.Info(new string('x', 60));
        Assert.True(File.Exists(Path.ChangeExtension(p, ".1.log")));
        Assert.True(new FileInfo(p).Length < 500 + 120);
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using System.Globalization;
using System.Text;

namespace DeskWall.Core.Diagnostics;

public sealed class RollingLog(string path, long maxBytes = 1_000_000)
{
    private readonly object _lock = new();
    public string? LastError { get; private set; }

    public static RollingLog Default() => new(Paths.InRuntime("deskwall.log"));

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}";
        LastError = text;
        var first = ex?.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        Write("ERROR", first is null ? text : $"{text} | {first}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length + line.Length > maxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException) { /* logging must never take the daemon down */ }
        }
    }
}
```

- [ ] **Step 4: Run tests, expect pass** (2).
- [ ] **Step 5: Commit** `git commit -m "RollingLog: append-only runtime log with size roll and LastError"`

---

### Task 5: `Startup` (Run key) (lane `lane/p2-plumbing`, Sonnet)

**Files:**
- Create: `src/DeskWall.Core/Startup.cs`
- Test: `tests/DeskWall.Core.Tests/StartupTests.cs`

**Interfaces:**
- Produces:

```csharp
/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry "DeskWall" -> "<exe>" run.</summary>
public static class Startup
{
    public const string ValueName = "DeskWall";
    public static void Install(string exePath, string? subKey = null);     // subKey overridable for tests
    public static void Uninstall(string? subKey = null);
    public static string? Installed(string? subKey = null);               // the command line, or null
}
```

Use `Microsoft.Win32.Registry` (AOT-safe; it is in the shared framework on Windows). Tests use a
private sub key under `HKCU\Software\DeskWallTests\Run-<guid>` and delete it after.

- [ ] **Step 1: Failing test**

```csharp
using DeskWall.Core;
using Microsoft.Win32;
using Xunit;

public class StartupTests
{
    [Fact]
    public void Install_Then_Uninstall_RoundTrips_In_A_Private_Key()
    {
        var sub = @"Software\DeskWallTests\Run-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.Null(Startup.Installed(sub));
            Startup.Install(@"C:\x\deskwall.exe", sub);
            Assert.Equal("\"C:\\x\\deskwall.exe\" run", Startup.Installed(sub));
            Startup.Uninstall(sub);
            Assert.Null(Startup.Installed(sub));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); Registry.CurrentUser.DeleteSubKeyTree(@"Software\DeskWallTests", throwOnMissingSubKey: false); }
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement**

```csharp
using Microsoft.Win32;

namespace DeskWall.Core;

public static class Startup
{
    public const string ValueName = "DeskWall";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Install(string exePath, string? subKey = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(subKey ?? RunKey, writable: true);
        k.SetValue(ValueName, $"\"{exePath}\" run", RegistryValueKind.String);
    }

    public static void Uninstall(string? subKey = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey ?? RunKey, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static string? Installed(string? subKey = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey ?? RunKey);
        return k?.GetValue(ValueName) as string;
    }
}
```

- [ ] **Step 4: Run tests, expect pass.**
- [ ] **Step 5: Commit** `git commit -m "Startup: HKCU Run key install/uninstall"`

Lane `lane/p2-plumbing` complete: rebase on `v1`, `dotnet test`, merge.

---

### Task 6: `HostWindow` and `WaitableTimer` (lane `lane/p2-host`, Opus)

**Files:**
- Create: `src/DeskWall.Daemon/Host/HostWindow.cs`, `src/DeskWall.Daemon/Host/WaitableTimer.cs`
- Modify: `src/DeskWall.Daemon/NativeMethods.txt` (append the names listed in Step 3)
- Test: none automated (a window and a message pump need the interactive session); Step 4 is a
  manual harness run through `deskwall host-test` that the task adds and Task 8 removes.

**Interfaces:**
- Consumes: `WakeReason`, `WakeKind` (Task 1), `Com.EnsureInitialized`.
- Produces:

```csharp
namespace DeskWall.Daemon.Host;

/// <summary>Hidden top-level window plus message pump. Everything the OS tells us arrives here and
/// is turned into a WakeReason. Must be created and pumped on one thread (the main thread).</summary>
public sealed unsafe class HostWindow : IDisposable
{
    public const uint WM_APP_WAKE = 0x8000 + 1;     // WPARAM = (int)WakeKind, posted by other threads
    public const uint WM_APP_TRAY = 0x8000 + 2;     // Shell_NotifyIcon callback message (Task 7)

    public HWND Handle { get; }
    public event Action<WakeReason>? Wake;          // raised on the pump thread
    public event Action<uint, nuint, nint>? TrayMessage;   // (msg, wParam, lParam) for Task 7
    public HostWindow();                            // RegisterClassEx + CreateWindowEx (WS_OVERLAPPED, never shown) + WTSRegisterSessionNotification
    /// <summary>Block until <paramref name="timer"/> fires or a message arrives; pump all pending
    /// messages; return the reasons raised (possibly empty when only unrelated messages came).</summary>
    public IReadOnlyList<WakeReason> WaitAndPump(WaitableTimer timer);
    /// <summary>Thread-safe: PostMessage(WM_APP_WAKE, kind).</summary>
    public void Post(WakeKind kind);
    public void Dispose();                          // WTSUnRegisterSessionNotification, DestroyWindow, UnregisterClass
}

/// <summary>CreateWaitableTimerEx wrapper. Absolute due time.</summary>
public sealed class WaitableTimer : IDisposable
{
    public SafeHandle Handle { get; }
    public void SetDue(DateTimeOffset dueUtc);      // SetWaitableTimer with a negative-relative or absolute FILETIME; CREATE_WAITABLE_TIMER_HIGH_RESOLUTION not needed
    public void Cancel();
}
```

Message mapping (spec 3.1):

| Message | WakeReason |
|---|---|
| `WM_DISPLAYCHANGE` | `DisplayChange` with detail `"{w}x{h}"` |
| `WM_SETTINGCHANGE` with lParam `"ImmersiveColorSet"` or wParam `SPI_SETDESKWALLPAPER` | `DisplayChange` (a wallpaper/theme change under us; re-apply) |
| `WM_WTSSESSION_CHANGE` with `WTS_SESSION_UNLOCK` or `WTS_REMOTE_CONNECT` or `WTS_CONSOLE_CONNECT` | `SessionUnlock` |
| `WM_POWERBROADCAST` with `PBT_APMRESUMEAUTOMATIC` | `Timer` (re-evaluate; the clock jumped) |
| `WM_APP_WAKE` | `(WakeKind)wParam` |
| `WM_APP_TRAY` | raises `TrayMessage`, no WakeReason |
| `WM_CLOSE`, `WM_QUERYENDSESSION`, `WM_ENDSESSION` | `Shutdown` |

- [ ] **Step 1: NativeMethods.txt additions**

```
RegisterClassExW
UnregisterClass
CreateWindowExW
DestroyWindow
DefWindowProc
PeekMessage
TranslateMessage
DispatchMessage
PostMessage
PostQuitMessage
GetModuleHandle
MsgWaitForMultipleObjectsEx
CreateWaitableTimerEx
SetWaitableTimer
CancelWaitableTimer
WTSRegisterSessionNotification
WTSUnRegisterSessionNotification
WNDCLASSEXW
MSG
WINDOW_STYLE
WINDOW_EX_STYLE
PEEK_MESSAGE_REMOVE_TYPE
QUEUE_STATUS_FLAGS
MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS
WM_DISPLAYCHANGE
WM_SETTINGCHANGE
WM_WTSSESSION_CHANGE
WM_POWERBROADCAST
WM_CLOSE
WM_QUERYENDSESSION
WM_ENDSESSION
WM_DESTROY
WTS_SESSION_UNLOCK
WTS_REMOTE_CONNECT
WTS_CONSOLE_CONNECT
NOTIFY_FOR_THIS_SESSION
PBT_APMRESUMEAUTOMATIC
SPI_SETDESKWALLPAPER
WAIT_OBJECT_0
WAIT_TIMEOUT
INFINITE
```

Build; rename per the generator's errors; record renames in the report.

- [ ] **Step 2: `WaitableTimer.cs`**

```csharp
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace DeskWall.Daemon.Host;

public sealed unsafe class WaitableTimer : IDisposable
{
    public SafeFileHandle Handle { get; }

    public WaitableTimer()
    {
        Handle = PInvoke.CreateWaitableTimerEx(null, (string?)null, 0, (uint)(SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_ALL_ACCESS));
        if (Handle.IsInvalid) throw new InvalidOperationException("CreateWaitableTimerEx failed");
    }

    public void SetDue(DateTimeOffset due)
    {
        // Absolute FILETIME (UTC, 100 ns ticks since 1601). Absolute, not relative, so a suspended
        // machine that resumes past the due time fires immediately instead of restarting the delay.
        long ft = due.UtcDateTime.ToFileTimeUtc();
        if (!PInvoke.SetWaitableTimer(Handle, &ft, 0, null, null, false)) throw new InvalidOperationException("SetWaitableTimer failed");
    }

    public void Cancel() => PInvoke.CancelWaitableTimer(Handle);

    public void Dispose() => Handle.Dispose();
}
```

`CreateWaitableTimerEx` name/overload shapes depend on the generator (`SafeFileHandle` vs `HANDLE`);
follow what it emits. The exact access-rights enum may be `TIMER_ALL_ACCESS` on a different enum;
the error message names it.

- [ ] **Step 3: `HostWindow.cs`**

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskWall.Core;
using DeskWall.Core.Scheduling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DeskWall.Daemon.Host;

public sealed unsafe class HostWindow : IDisposable
{
    public const uint WM_APP_WAKE = 0x8001;
    public const uint WM_APP_TRAY = 0x8002;
    private const string ClassName = "DeskWallHost";

    private static HostWindow? s_instance;       // one per process; WndProc is static
    private readonly List<WakeReason> _pending = new();
    private ushort _atom;

    public HWND Handle { get; private set; }
    public event Action<WakeReason>? Wake;
    public event Action<uint, nuint, nint>? TrayMessage;

    public HostWindow()
    {
        if (s_instance is not null) throw new InvalidOperationException("one HostWindow per process");
        s_instance = this;
        Com.EnsureInitialized();
        var hinst = PInvoke.GetModuleHandle((string?)null);
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = (HINSTANCE)hinst.DangerousGetHandle(),
                lpszClassName = cls,
            };
            _atom = PInvoke.RegisterClassEx(&wc);
            if (_atom == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            Handle = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, cls, cls, WINDOW_STYLE.WS_OVERLAPPED, 0, 0, 0, 0, HWND.Null, null, hinst, null);
            if (Handle.IsNull) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
        PInvoke.WTSRegisterSessionNotification(Handle, PInvoke.NOTIFY_FOR_THIS_SESSION);
    }

    public void Post(WakeKind kind) => PInvoke.PostMessage(Handle, WM_APP_WAKE, (nuint)(int)kind, 0);

    public IReadOnlyList<WakeReason> WaitAndPump(WaitableTimer timer)
    {
        _pending.Clear();
        var h = (HANDLE)timer.Handle.DangerousGetHandle();
        // Blocks until the timer is signalled or any message is queued. No polling.
        PInvoke.MsgWaitForMultipleObjectsEx(1, &h, PInvoke.INFINITE, QUEUE_STATUS_FLAGS.QS_ALLINPUT, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
        // Was it the timer? WaitForSingleObject with 0 timeout tells us without consuming a message.
        if (PInvoke.WaitForSingleObject(h, 0) == WAIT_EVENT.WAIT_OBJECT_0) _pending.Add(new WakeReason(WakeKind.Timer));
        MSG msg;
        while (PInvoke.PeekMessage(&msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            if (msg.message == PInvoke.WM_QUIT) { _pending.Add(new WakeReason(WakeKind.Shutdown)); break; }
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
        var result = _pending.ToList();
        foreach (var r in result) Wake?.Invoke(r);
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        var self = s_instance;
        if (self is not null)
        {
            switch (msg)
            {
                case PInvoke.WM_DISPLAYCHANGE:
                    self._pending.Add(new WakeReason(WakeKind.DisplayChange, $"{(int)(lParam.Value & 0xFFFF)}x{(int)((lParam.Value >> 16) & 0xFFFF)}"));
                    return (LRESULT)0;
                case PInvoke.WM_SETTINGCHANGE:
                    if ((uint)wParam.Value == PInvoke.SPI_SETDESKWALLPAPER || (lParam.Value != 0 && new PCWSTR((char*)lParam.Value).ToString() == "ImmersiveColorSet"))
                        self._pending.Add(new WakeReason(WakeKind.DisplayChange, "settingchange"));
                    return (LRESULT)0;
                case PInvoke.WM_WTSSESSION_CHANGE:
                    if ((uint)wParam.Value is PInvoke.WTS_SESSION_UNLOCK or PInvoke.WTS_REMOTE_CONNECT or PInvoke.WTS_CONSOLE_CONNECT)
                        self._pending.Add(new WakeReason(WakeKind.SessionUnlock, ((uint)wParam.Value).ToString()));
                    return (LRESULT)0;
                case PInvoke.WM_POWERBROADCAST:
                    if ((uint)wParam.Value == PInvoke.PBT_APMRESUMEAUTOMATIC) self._pending.Add(new WakeReason(WakeKind.Timer, "resume"));
                    return (LRESULT)1;
                case WM_APP_WAKE:
                    self._pending.Add(new WakeReason((WakeKind)(int)wParam.Value, "posted"));
                    return (LRESULT)0;
                case WM_APP_TRAY:
                    self.TrayMessage?.Invoke(msg, wParam.Value, lParam.Value);
                    return (LRESULT)0;
                case PInvoke.WM_CLOSE:
                case PInvoke.WM_QUERYENDSESSION:
                case PInvoke.WM_ENDSESSION:
                    self._pending.Add(new WakeReason(WakeKind.Shutdown, msg.ToString()));
                    return msg == PInvoke.WM_QUERYENDSESSION ? (LRESULT)1 : (LRESULT)0;
            }
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (!Handle.IsNull)
        {
            PInvoke.WTSUnRegisterSessionNotification(Handle);
            PInvoke.DestroyWindow(Handle);
            Handle = HWND.Null;
        }
        if (_atom != 0) { fixed (char* cls = ClassName) PInvoke.UnregisterClass(cls, PInvoke.GetModuleHandle((string?)null)); _atom = 0; }
        s_instance = null;
    }
}
```

Add `WaitForSingleObject`, `WAIT_EVENT`, `WM_QUIT` to `NativeMethods.txt` too.

- [ ] **Step 4: Manual harness**

Add to `Program.cs` (temporary; Task 8 replaces it):

```csharp
case "host-test":
{
    using var win = new HostWindow();
    using var timer = new WaitableTimer();
    Console.WriteLine("host-test: change resolution, lock/unlock, or wait 5 s. Ctrl+C to stop.");
    for (var i = 0; i < 6; i++)
    {
        timer.SetDue(DateTimeOffset.UtcNow.AddSeconds(5));
        foreach (var r in win.WaitAndPump(timer)) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} wake: {r}");
    }
    return 0;
}
```

Run `dotnet run --project src/DeskWall.Daemon -- host-test` from a console. Expected: a `Timer`
line every 5 s; changing display scale in Settings during the run prints `DisplayChange`;
locking and unlocking prints `SessionUnlock`. Paste the output into the report. Also record
`Footprint.Current().Short()` printed at the end of the loop: this is the first look at the
resident footprint (JIT, so only indicative).

- [ ] **Step 5: Commit** `git commit -m "Host: hidden window + message pump -> WakeReason; waitable timer"`

---

### Task 7: `TrayIcon` (lane `lane/p2-host`, Opus)

**Files:**
- Create: `src/DeskWall.Daemon/Host/TrayIcon.cs`, `src/DeskWall.Daemon/deskwall.ico` (a 16/32/48 px icon; a plain white rounded square on transparent is fine for v1, generated once by a small script in this task and committed as a binary)
- Modify: `src/DeskWall.Daemon/DeskWall.Daemon.csproj` (`<ApplicationIcon>deskwall.ico</ApplicationIcon>` and embed as a resource), `src/DeskWall.Daemon/NativeMethods.txt`

**Interfaces:**
- Consumes: `HostWindow.Handle`, `HostWindow.TrayMessage`, `HostWindow.WM_APP_TRAY`.
- Produces:

```csharp
public enum TrayCommand { OpenDesigner = 1, RefreshNow = 2, TogglePause = 3, Exit = 4 }

/// <summary>Shell_NotifyIcon wrapper. Menu: Open designer, Refresh now, Pause/Resume, Exit. Left click = Open designer.</summary>
public sealed class TrayIcon : IDisposable
{
    public TrayIcon(HostWindow host);                    // NIM_ADD with NIF_MESSAGE|NIF_ICON|NIF_TIP|NIF_SHOWTIP, uCallbackMessage = WM_APP_TRAY, NIM_SETVERSION 4
    public event Action<TrayCommand>? Command;           // raised on the pump thread
    public bool Paused { get; set; }                     // flips the menu text
    public void SetTooltip(string text);                 // truncated to 127 chars; NIM_MODIFY
    public void Dispose();                               // NIM_DELETE
}
```

Tooltip content is Task 8's job; this task only transports it. On `WM_APP_TRAY` with
`LOWORD(lParam) == WM_CONTEXTMENU` or `WM_RBUTTONUP`: `SetForegroundWindow(host)` then
`TrackPopupMenuEx` at the cursor with the four items; the chosen id raises `Command`. With
`WM_LBUTTONUP` or `NIN_SELECT`: raise `OpenDesigner`. `NIN_POPUPOPEN`: raise nothing here; Task 8
refreshes the tooltip on every tick anyway, and the shell shows the current tip.

- [ ] **Step 1: NativeMethods.txt additions**

```
Shell_NotifyIcon
NOTIFYICONDATAW
NOTIFY_ICON_DATA_FLAGS
NOTIFY_ICON_MESSAGE
NOTIFYICON_VERSION_4
CreatePopupMenu
DestroyMenu
AppendMenu
TrackPopupMenuEx
GetCursorPos
SetForegroundWindow
LoadImage
LoadIcon
DestroyIcon
MENU_ITEM_FLAGS
TRACK_POPUP_MENU_FLAGS
WM_CONTEXTMENU
WM_RBUTTONUP
WM_LBUTTONUP
WM_COMMAND
NIN_SELECT
NIN_POPUPOPEN
IMAGE_FLAGS
GDI_IMAGE_TYPE
```

- [ ] **Step 2: Implement**

```csharp
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DeskWall.Daemon.Host;

public enum TrayCommand { OpenDesigner = 1, RefreshNow = 2, TogglePause = 3, Exit = 4 }

public sealed unsafe class TrayIcon : IDisposable
{
    private const uint Id = 1;
    private readonly HostWindow _host;
    private HICON _icon;
    private bool _added;

    public event Action<TrayCommand>? Command;
    public bool Paused { get; set; }

    public TrayIcon(HostWindow host)
    {
        _host = host;
        _icon = LoadAppIcon();
        var nid = Data();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        nid.uCallbackMessage = HostWindow.WM_APP_TRAY;
        nid.hIcon = _icon;
        SetTip(ref nid, "DeskWall");
        _added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &nid);
        nid.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &nid);
        host.TrayMessage += OnTrayMessage;
    }

    private NOTIFYICONDATAW Data() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _host.Handle, uID = Id };

    private static void SetTip(ref NOTIFYICONDATAW nid, string text)
    {
        if (text.Length > 127) text = text[..127];
        var span = nid.szTip.AsSpan();
        span.Clear();
        text.AsSpan().CopyTo(span);
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var nid = Data();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        SetTip(ref nid, text);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &nid);
    }

    private void OnTrayMessage(uint msg, nuint wParam, nint lParam)
    {
        var evt = (uint)(lParam & 0xFFFF);   // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = x,y
        switch (evt)
        {
            case PInvoke.WM_CONTEXTMENU:
            case PInvoke.WM_RBUTTONUP:
                ShowMenu();
                break;
            case PInvoke.WM_LBUTTONUP:
            case PInvoke.NIN_SELECT:
                Command?.Invoke(TrayCommand.OpenDesigner);
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            Append(menu, TrayCommand.OpenDesigner, "Open designer");
            Append(menu, TrayCommand.RefreshNow, "Refresh now");
            Append(menu, TrayCommand.TogglePause, Paused ? "Resume" : "Pause");
            Append(menu, TrayCommand.Exit, "Exit");
            System.Drawing.Point pt; PInvoke.GetCursorPos((Windows.Win32.Foundation.POINT*)&pt);
            PInvoke.SetForegroundWindow(_host.Handle);   // required or the menu does not dismiss on outside click
            var chosen = PInvoke.TrackPopupMenuEx(menu, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN), pt.X, pt.Y, _host.Handle, null);
            if (chosen.Value != 0) Command?.Invoke((TrayCommand)chosen.Value);
        }
        finally { PInvoke.DestroyMenu(menu); }
    }

    private static void Append(HMENU menu, TrayCommand id, string text)
    {
        fixed (char* p = text) PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, (nuint)(int)id, p);
    }

    private static HICON LoadAppIcon()
    {
        // The exe's own icon (ApplicationIcon in the csproj), resource id 32512 (IDI_APPLICATION slot for the first icon group).
        var hinst = PInvoke.GetModuleHandle((string?)null);
        var h = PInvoke.LoadIcon(hinst, (PCWSTR)(char*)32512);
        return h.IsNull ? PInvoke.LoadIcon(HINSTANCE.Null, (PCWSTR)(char*)32512) : h;   // fall back to the stock application icon
    }

    public void Dispose()
    {
        _host.TrayMessage -= OnTrayMessage;
        if (_added) { var nid = Data(); PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &nid); _added = false; }
    }
}
```

`GetCursorPos` takes a `POINT*`; use CsWin32's `POINT`, not `System.Drawing.Point` (the snippet's
cast is a reminder that the two are layout-identical; prefer the generated type). `TrackPopupMenuEx`
returns `BOOL` in CsWin32 even with `TPM_RETURNCMD` (the raw return is the command id); if the
generator types it as `BOOL`, read `.Value` as the id.

- [ ] **Step 3: Icon file**

Generate once with PowerShell and commit the binary:

```powershell
Add-Type -AssemblyName System.Drawing
$sizes = 16, 32, 48
$pngs = foreach ($s in $sizes) {
  $b = New-Object Drawing.Bitmap $s, $s; $g = [Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'
  $g.Clear([Drawing.Color]::Transparent)
  $path = New-Object Drawing.Drawing2D.GraphicsPath; $r = [int]($s * 0.25); $m = [int]($s * 0.08); $w = $s - 2 * $m
  $path.AddArc($m, $m, $r, $r, 180, 90); $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90); $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90); $path.CloseFigure()
  $g.FillPath([Drawing.Brushes]::White, $path); $g.Dispose()
  $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); $ms.ToArray()
}
$bw = New-Object IO.BinaryWriter ([IO.File]::Create("src\DeskWall.Daemon\deskwall.ico"))
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) { $bw.Write([byte]$sizes[$i]); $bw.Write([byte]$sizes[$i]); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset); $offset += $pngs[$i].Length }
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
```

Add `<ApplicationIcon>deskwall.ico</ApplicationIcon>` to the Daemon csproj. Add
`src/DeskWall.Daemon/deskwall.ico` to git explicitly (`git add -f` is not needed; the root
`.gitignore` only ignores `/*.ico` at the root and `poc/*.ico`).

- [ ] **Step 4: Manual harness**

Extend the Task 6 `host-test` case: create a `TrayIcon`, subscribe `Command` to print the
command, call `SetTooltip($"DeskWall test {i}")` each loop. Run; right-click the tray icon;
choose each item; hover to see the tooltip update. Paste the printed commands into the report.
Verify with Task Manager that after the loop the process shows no window in the taskbar.

- [ ] **Step 5: Commit** `git commit -m "Host: tray icon with menu, tooltip and app icon"`

Lane `lane/p2-host` complete: rebase on `v1`, `dotnet build` zero warnings, merge.

---

### Task 8: `DaemonLoop`, `run`, `install`, `uninstall` (controller)

**Files:**
- Create: `src/DeskWall.Daemon/DaemonLoop.cs`
- Modify: `src/DeskWall.Daemon/Program.cs` (remove `host-test`; add `run`, `install`, `uninstall`, `--home`)
- Test: `tests/DeskWall.Core.Tests/Scheduling/TickPlanTests.cs` for the pure part below.

**Interfaces:**
- Consumes: everything above.
- Produces: `DaemonLoop(RollingLog log, LayoutStore store, IClock clock, bool tray)` with `int Run()`.

The loop, per spec 3.1:

```csharp
using DeskWall.Core;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Scheduling;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using DeskWall.Core.Wallpaper;
using DeskWall.Daemon.Host;

namespace DeskWall.Daemon;

public sealed class DaemonLoop(RollingLog log, LayoutStore store, IClock clock, bool tray)
{
    private sealed record Active(MonitorInfo Monitor, LayoutResolution Resolution, List<ISource> Sources, SourceRegistry Registry, Scheduler Scheduler, TickRunner Runner, string SignatureKey);

    private Active? _active;
    private bool _paused;
    private DateTimeOffset _nextWake;
    private TickTimings? _last;

    public int Run()
    {
        using var win = new HostWindow();
        using var timer = new WaitableTimer();
        using var trayIcon = tray ? new TrayIcon(win) : null;
        using var watcher = new LayoutWatcher(store, () => win.Post(WakeKind.LayoutChanged));
        var exit = false;
        if (trayIcon is not null) trayIcon.Command += cmd =>
        {
            switch (cmd)
            {
                case TrayCommand.RefreshNow: win.Post(WakeKind.Manual); break;
                case TrayCommand.TogglePause: _paused = !_paused; trayIcon.Paused = _paused; win.Post(WakeKind.Manual); break;
                case TrayCommand.Exit: exit = true; win.Post(WakeKind.Shutdown); break;
                case TrayCommand.OpenDesigner: Designer.Open(log); break;   // Phase 5; until then logs "designer not installed"
            }
        };

        log.Info($"daemon start pid {Environment.ProcessId}");
        WallpaperSetter.RecordRestorePoint();
        Tick(new WakeReason(WakeKind.Manual, "start"), force: true);

        while (!exit)
        {
            timer.SetDue(_nextWake);
            var reasons = win.WaitAndPump(timer);
            if (reasons.Any(r => r.Kind == WakeKind.Shutdown)) break;
            if (reasons.Count == 0) continue;                       // unrelated message; back to sleep, no tick
            var display = reasons.Any(r => r.Kind is WakeKind.DisplayChange);
            var layout = reasons.Any(r => r.Kind == WakeKind.LayoutChanged);
            if (display) Thread.Sleep(2000);                        // spec 3.1: let Explorer finish re-laying the desktop
            Tick(reasons[0], force: display || layout || reasons.Any(r => r.Kind == WakeKind.Manual));
            trayIcon?.SetTooltip(Tooltip());
        }
        log.Info("daemon stop");
        return 0;
    }

    private void Tick(WakeReason why, bool force)
    {
        try
        {
            if (_paused) { _nextWake = clock.Now + Scheduler.MaxDelay; return; }
            var monitor = Monitors.Enumerate().FirstOrDefault(m => m.IsPrimary);
            if (monitor is null) { log.Warn("no primary monitor"); _nextWake = clock.Now.AddMinutes(1); return; }
            if (_active is null || _active.SignatureKey != monitor.Signature.Key || force && why.Kind == WakeKind.LayoutChanged)
                _active = Activate(monitor);
            if (_active is null) { _nextWake = clock.Now.AddMinutes(1); return; }
            _last = _active.Runner.RunAsync(force, apply: true, CancellationToken.None).GetAwaiter().GetResult();
            log.Info($"tick {why}: {(_last.Skipped ? "skipped" : $"redrawn {_last.Redrawn}")} total {_last.TotalMs} ms cpu {_last.CpuMs:N0} ms");
            _nextWake = _active.Scheduler.NextWake(clock.Now);
        }
        catch (Exception ex)
        {
            log.Error($"tick {why} failed", ex);                   // spec 3.2: previous wallpaper stays
            _nextWake = clock.Now.AddMinutes(1);
        }
        finally
        {
            Footprint.Trim();
        }
    }

    private Active? Activate(MonitorInfo monitor)
    {
        var res = store.Resolve(monitor.Signature);
        if (res is null) { log.Warn($"no layout for {monitor.Signature.Key}; base only is not possible without a layout, waiting"); return null; }
        if (res.Scaled) log.Info($"scaled layout {res.SourcePath} from {res.SourceSignature.Key} to {monitor.Signature.Key}");
        var registry = new SourceRegistry();
        var sources = res.Layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        return new Active(monitor, res, sources, registry, new Scheduler(sources, registry),
            new TickRunner(res.Layout, sources, registry, clock, monitor), monitor.Signature.Key);
    }

    private string Tooltip()
    {
        var f = Footprint.Current();
        var next = _nextWake.ToLocalTime().ToString("HH:mm");
        var last = _last is null ? "" : $" . tick {_last.CpuMs:0} ms CPU";
        var err = log.LastError is null ? "" : " . ERROR see log";
        var state = _paused ? "Paused" : $"Next {next}";
        return $"DeskWall: {state}{last} . {f.WorkingSetBytes / 1048576.0:0.0} MB . GPU not used{err}";
    }
}
```

`LayoutWatcher` (same file or `Host/LayoutWatcher.cs`): one `FileSystemWatcher` per directory in
`store.WatchPaths`, filter `*.json`, `Changed|Created|Renamed|Deleted`, debounced 300 ms with a
`System.Threading.Timer`, then invoke the callback. `FileSystemWatcher` is AOT-safe and costs one
thread while idle only when events arrive (it uses the thread pool's I/O completion, not a
dedicated thread).

`Designer.Open(log)`: if `DeskWall.Designer.exe` exists next to the daemon, `Process.Start` it;
else `log.Warn("designer not installed")`. Phase 5 fills in the exe.

`Program.cs` final shape:

```
deskwall                     -> run
deskwall run [--no-tray]     -> DaemonLoop
deskwall tick [...]          -> Phase 1 (unchanged), but now resolves the layout through LayoutStore like the daemon
deskwall install             -> Startup.Install(own exe path); WallpaperSetter.RecordRestorePoint(); Process.Start(self, "run")
deskwall uninstall           -> Startup.Uninstall(); stop a running daemon (PostMessage WM_CLOSE to the DeskWallHost window via FindWindow); WallpaperSetter.Restore()
deskwall paths               -> unchanged
```

`--home <dir>` on every command sets `DESKWALL_HOME` before anything touches `Paths` (lets the
budget test run against a scratch runtime dir). `tick` and `run` share `LayoutStore.Default()`;
`tick --layout <path>` still bypasses the store for scripting.

Single instance: `run` opens a named mutex `Local\DeskWall.Daemon`; if it already exists, post
`WM_APP_WAKE(Manual)` to the existing host window and exit 0.

- [ ] **Step 1: Failing test for the pure decision logic**

Extract the "what to do with a set of reasons" decision into a static function and test it:

```csharp
public static class TickPlan
{
    public sealed record Decision(bool Tick, bool Force, bool DelayForExplorer, bool Reactivate, bool Shutdown);
    public static Decision From(IReadOnlyList<WakeReason> reasons)
    {
        if (reasons.Any(r => r.Kind == WakeKind.Shutdown)) return new(false, false, false, false, true);
        if (reasons.Count == 0) return new(false, false, false, false, false);
        var display = reasons.Any(r => r.Kind == WakeKind.DisplayChange);
        var layout = reasons.Any(r => r.Kind == WakeKind.LayoutChanged);
        var manual = reasons.Any(r => r.Kind == WakeKind.Manual);
        return new(true, display || layout || manual, display, display || layout, false);
    }
}
```

Tests: empty -> no tick; Timer -> tick, not forced; DisplayChange -> tick, forced, delay,
reactivate; LayoutChanged -> forced, reactivate, no delay; Shutdown wins over everything.
Put `TickPlan` in `DeskWall.Core.Scheduling` so the test project reaches it, and have
`DaemonLoop.Run` use it instead of the inline booleans shown above.

- [ ] **Step 2: Implement, build, `dotnet test` green.**

- [ ] **Step 3: Live run**

```powershell
Disable-ScheduledTask -TaskName 'DeskWall Tick'
dotnet build src\DeskWall.Daemon -c Release
$exe = "src\DeskWall.Daemon\bin\Release\net10.0-windows10.0.19041.0\deskwall.exe"
# register the current layout for the current display first:
& $exe layouts set layouts\clock-disks.json      # small helper command: LayoutStore.Set(current primary signature, path); add it to Program.cs
Start-Process $exe run
Start-Sleep 3
Get-Process deskwall | Select-Object Id, WorkingSet64, PrivateMemorySize64, Handles, Threads
```

Expected: the tray icon appears; the wallpaper updates within a second; the tooltip shows
`Next HH:mm . tick N ms CPU . X MB . GPU not used`. Wait two minutes, re-run `Get-Process`,
and check the log shows one `tick Timer` per minute with `redrawn 1` (the clock) and nothing
between. Right-click, Pause, confirm ticks stop; Resume; Exit. Re-enable the POC task.

- [ ] **Step 4: Commit** `git commit -m "Daemon: run loop with host window, tray, scheduler, hot reload; install/uninstall"`

---

### Task 9: Footprint measurement and Phase 2 exit (controller)

**Files:**
- Modify: `docs/superpowers/plans/2026-09-20-phase1-spike-results.md` (append `## Phase 2 measurements`)

- [ ] **Step 1: Measure** (JIT if AOT is still blocked; AOT as soon as the linker exists)

Run the daemon for 10 minutes with the POC task disabled. Record from `Get-Process deskwall`
every 2 minutes: WorkingSet64, PrivateMemorySize64, Handles, Threads, and `TotalProcessorTime`
deltas. Record from the log: per-tick total and cpu. Record whether CPU between ticks is zero
(Task Manager "CPU" column shows 0 or Process Explorer's context-switch delta is only the
per-minute wake).

Fill the budget table:

| Measure | Budget | JIT measured | AOT measured |
|---|---|---|---|
| Idle private working set after trim | 10 MB | | |
| Idle CPU between wakes | 0 | | |
| Clock-only tick wall | 60 ms | | |
| Clock-only tick CPU | 40 ms | | |
| Cold start to first wallpaper | 500 ms | | |
| Handles at idle | under 100 | | |
| Threads at idle | under 5 | | |

Any JIT number that already passes is a pass. Any JIT number that fails is inconclusive until
AOT; note it, do not "fix" it blind.

- [ ] **Step 2: Phase 2 exit criteria**

- [ ] `dotnet test` green on `v1`.
- [ ] Daemon ran 10 minutes: one tick per minute, no errors in the log, tray tooltip correct.
- [ ] Display change (RDP connect at 1920x1200, or a resolution change) re-rendered a scaled layout within 3 s and logged `scaled layout ...`.
- [ ] Layout file edit re-rendered within 1 s.
- [ ] `install` then sign out/in starts the daemon; `uninstall` removes the Run key, stops the daemon, restores the wallpaper.
- [ ] Budget table filled; AOT column filled if the linker is available.
- [ ] POC task re-enabled (it still owns the covers and shortcuts until Phase 3).

## Self-review notes

- Spec 3.1 lifecycle steps 1 to 5: Tasks 6, 8 (window, first tick, wait, wake kinds, trim).
- Spec 3.2 error policy: `DaemonLoop.Tick` catch and log; `RollingLog.LastError` feeds the tooltip.
- Spec 3.3 tray: Task 7 transport, Task 8 content; four menu items; on by default (`--no-tray` to disable; the designer's checkbox in Phase 5 writes a setting the daemon reads at start, added then).
- Spec 3.4 install: Task 5 + Task 8; restore point recorded at first `run` and at `install`.
- Spec 5 store/unknown signature/hot reload: Task 3 + `LayoutWatcher` in Task 8.
- Not in this phase: the designer's "save scaled layout for this signature" offer (Phase 5); shortcuts (Phase 3).
- Type names match Phase 1 exactly (`TickRunner` ctor, `MonitorInfo`, `SourceFactory.Create(SourceDef, IClock)`).
