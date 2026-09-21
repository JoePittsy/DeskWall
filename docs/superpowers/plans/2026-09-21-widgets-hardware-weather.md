# Widgets: hardware dials, weather, Tailscale — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an in-process `hardware` source (CPU, RAM, NVML GPU, 5-minute averages), a `dial` component, a `runtime:` image path prefix, an Open-Meteo weather recipe with a WMO-code icon folder, a Tailscale state recipe, and a `column-system.json` starter that uses them, then register that starter live on JOES-PC and measure its cost.

**Architecture:** Every addition follows an existing seam. The source is a `PeriodicSource` whose own `System.Threading.Timer` samples every 10 s into fixed ring buffers and publishes averages once a minute, so the daemon's scheduler is untouched. The component is a sibling of `bar` at every layer: `DialDef` (Layout), `ResolvedDial` (Resolve), `Surface.DrawArc` + `FrameRenderer` case (Render), `PropertySchema` row (Designer), golden PNG (tests). Weather and Tailscale are layout JSON on the existing `http` and `command` sources.

**Tech Stack:** C# / .NET 10, native AOT (`IsAotCompatible`), CsWin32 with `allowMarshaling: false`, Direct2D path geometry, NVML via `NativeLibrary` + unmanaged function pointers, xUnit, WPF designer (JIT).

**Spec:** `docs/superpowers/specs/2026-09-21-widgets-hardware-weather-design.md`

## Global Constraints

- `CLAUDE.md` applies in full: nothing resident but `deskwall.exe`; measured, not eyeballed; design doctrine gates; right-hand margin only; tests run under `DESKWALL_HOME` and never write into the real runtime dir.
- Core stays native-AOT safe: `dotnet build` must report **0 Warning(s)**. No reflection over defs, no `DllImport` with marshalled types; NVML calls go through `NativeLibrary.TryLoad` + `delegate* unmanaged[Cdecl]`.
- CsWin32 shapes: COM methods return `void` and throw; static entry points return `HRESULT`. New declarations go in `src/DeskWall.Core/NativeMethods.txt`; verified shapes are recorded in `docs/superpowers/plans/2026-09-20-phase1-spike-results.md` under "CsWin32 shapes verified".
- The D2D factory is `MULTI_THREADED` and xUnit parallelisation is off in the Core test assembly; do not change either.
- Content keys must quantise noisy values: fractions to 3 decimals (`Math.Round(x, 3).ToString("R")`), temperatures to integers.
- `.cs` files are UTF-8; keep the existing BOM state of any file you edit (most Core files have none; `SurfaceTests.cs` has one). CRLF line endings. No non-ASCII in `.ps1`.
- Branches: `v1` is the integration branch; lanes are `lane/<name>`. Lanes do not touch the live desktop, the real `%LOCALAPPDATA%\DeskWall`, or the designer window; the controller does Task 6.
- Every lane writes a report to `.superpowers/sdd/2026-09-21-widgets/lane-<name>-report.md`: what was verified (commands and output), deviations, concerns. Subagents do not spawn agents.
- Owner rulings not to re-open: no CPU temperature; sample 10 s, paint on the minute; Leeds coordinates in the layout; Tailscale only, up or down; approach 1 (dial + in-process source).

---

## File map

| Task | Creates | Modifies |
|---|---|---|
| 1 hardware | `src/DeskWall.Core/Sources/Hardware/RollingWindow.cs`, `IHardwareReader.cs`, `Win32HardwareReader.cs`, `NvmlGpuReader.cs`, `HardwareSource.cs`; `tests/.../Sources/RollingWindowTests.cs`, `HardwareSourceTests.cs` | `SourceFactory.cs`, `NativeMethods.txt` (GetSystemTimes, GlobalMemoryStatusEx, MEMORYSTATUSEX, FILETIME), `docs/sources.md`, `tests/.../Sources/SourceFactoryTests.cs` |
| 2 dial | `tests/.../Goldens/layouts/dial-states.json`, `tests/.../Goldens/dial-states.png` | `ComponentDef.cs`, `Resolved.cs`, `LayoutResolver.cs`, `LayoutScaler.cs`, `Surface.cs`, `FrameRenderer.cs`, `NativeMethods.txt` (path geometry), `tests/.../Resolve/LayoutResolverTests.cs`, `ResolvedTests.cs`, `Goldens/GoldenTests.cs`, `Layout/LayoutScalerTests.cs`, `docs/layout-format.md`, spike results doc |
| 3 runtime: prefix | — | `LayoutResolver.cs`, `tests/.../Resolve/LayoutResolverTests.cs`, `docs/layout-format.md` |
| 4 recipes | `assets/weather/*.png`, `assets/weather/LICENSE`, `assets/weather/README.md`, `layouts/column-system.json`, `tests/.../Layout/StarterLayoutTests.cs` | `layouts/README.md`, `docs/layout-format.md` (examples 11, 12) |
| 5 designer | — | `src/DeskWall.Designer/Model/PropertySchema.cs`, `Views/LayersPanel.xaml.cs`, `Views/FirstRun.xaml.cs`, `DeskWall.Designer.csproj`, `tests/DeskWall.Designer.Tests/PropertySchemaTests.cs` |
| 6 controller | — | live runtime dir, `docs/architecture.md` |

Lanes: Task 1 = `lane/w-hardware` (Opus), Task 2 + 3 = `lane/w-dial` (Opus), Task 4 = `lane/w-recipes` (Sonnet), Task 5 = `lane/w-designer` (Sonnet, after Task 2 merges), Task 6 = controller. Cap: 5 agents total including one Opus reviewer, at most 3 concurrent.

---

### Task 1: `hardware` source

**Files:**
- Create: `src/DeskWall.Core/Sources/Hardware/RollingWindow.cs`
- Create: `src/DeskWall.Core/Sources/Hardware/IHardwareReader.cs`
- Create: `src/DeskWall.Core/Sources/Hardware/Win32HardwareReader.cs`
- Create: `src/DeskWall.Core/Sources/Hardware/NvmlGpuReader.cs`
- Create: `src/DeskWall.Core/Sources/Hardware/HardwareSource.cs`
- Modify: `src/DeskWall.Core/Sources/SourceFactory.cs` (switch + `DefaultEvery`)
- Modify: `src/DeskWall.Core/NativeMethods.txt` (append `GetSystemTimes`, `GlobalMemoryStatusEx`, `MEMORYSTATUSEX`, `FILETIME`)
- Test: `tests/DeskWall.Core.Tests/Sources/RollingWindowTests.cs`, `tests/DeskWall.Core.Tests/Sources/HardwareSourceTests.cs`, add a case to `tests/DeskWall.Core.Tests/Sources/SourceFactoryTests.cs`
- Docs: `docs/sources.md` new `## \`hardware\`` section between `system` and `command`

**Interfaces:**
- Produces: `HardwareSource : PeriodicSource`, `HardwareSource.FromDef(SourceDef def)`; published fields exactly as spec section 3 (`cpu`, `cpuPct`, `cpuNow`, `ram`, `ramPct`, `ramUsedGB`, `ramTotalGB`, `gpu`, `gpuPct`, `gpuNow`, `gpuMemory`, `gpuTempC`, `gpuTempFraction`, `samples`, `window`). Task 4's layout binds `hardware.cpu`, `hardware.cpuPct`, `hardware.gpu`, `hardware.gpuPct`, `hardware.ram`, `hardware.ramPct`, `hardware.gpuTempFraction`, `hardware.gpuTempC`.
- Consumes: `PeriodicSource(string name, TimeSpan every)`, `RecordValue`, `NumberValue`, `SourceDef.Settings` (`Dictionary<string,string>`).

- [ ] **Step 1: RollingWindow failing tests**

```csharp
// tests/DeskWall.Core.Tests/Sources/RollingWindowTests.cs
using DeskWall.Core.Sources.Hardware;
using Xunit;

public class RollingWindowTests
{
    [Fact]
    public void Empty_Has_No_Average()
    {
        var w = new RollingWindow(3);
        Assert.Equal(0, w.Count);
        Assert.Null(w.Average);
    }

    [Fact]
    public void Averages_What_It_Holds()
    {
        var w = new RollingWindow(3);
        w.Add(0.2); w.Add(0.4);
        Assert.Equal(2, w.Count);
        Assert.Equal(0.3, w.Average!.Value, 9);
    }

    [Fact]
    public void Wraps_Dropping_The_Oldest()
    {
        var w = new RollingWindow(3);
        w.Add(1); w.Add(2); w.Add(3); w.Add(10);   // 1 falls out
        Assert.Equal(3, w.Count);
        Assert.Equal(5, w.Average!.Value, 9);
    }

    [Fact]
    public void Latest_Is_The_Last_Added()
    {
        var w = new RollingWindow(2);
        Assert.Null(w.Latest);
        w.Add(7); w.Add(8);
        Assert.Equal(8, w.Latest);
    }

    [Fact]
    public void Capacity_Below_One_Is_One()
    {
        var w = new RollingWindow(0);
        w.Add(4); w.Add(5);
        Assert.Equal(1, w.Count);
        Assert.Equal(5, w.Average);
    }
}
```

- [ ] **Step 2: Run, expect compile failure (type missing)**

Run: `dotnet test tests/DeskWall.Core.Tests --filter FullyQualifiedName~RollingWindowTests`
Expected: build error `RollingWindow` not found.

- [ ] **Step 3: Implement RollingWindow**

```csharp
// src/DeskWall.Core/Sources/Hardware/RollingWindow.cs
namespace DeskWall.Core.Sources.Hardware;

/// <summary>Fixed-capacity ring of doubles. Allocates once; Add/Average/Latest never allocate.
/// Not thread-safe on its own: HardwareSource guards every window under one lock.</summary>
public sealed class RollingWindow(int capacity)
{
    private readonly double[] _slots = new double[Math.Max(1, capacity)];
    private int _next;

    public int Count { get; private set; }

    public void Add(double value)
    {
        _slots[_next] = value;
        _next = (_next + 1) % _slots.Length;
        if (Count < _slots.Length) Count++;
    }

    public double? Average
    {
        get
        {
            if (Count == 0) return null;
            double sum = 0;
            for (var i = 0; i < Count; i++) sum += _slots[i];
            return sum / Count;
        }
    }

    public double? Latest => Count == 0 ? null : _slots[(_next - 1 + _slots.Length) % _slots.Length];
}
```

- [ ] **Step 4: Run RollingWindowTests, expect 5 passed**

- [ ] **Step 5: Reader interface + HardwareSource failing tests**

```csharp
// src/DeskWall.Core/Sources/Hardware/IHardwareReader.cs
namespace DeskWall.Core.Sources.Hardware;

/// <summary>Raw cumulative CPU times in 100 ns units, as GetSystemTimes reports them. Idle is
/// included in Kernel.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User)
{
    /// <summary>Load between two readings, 0..1; null when nothing elapsed.</summary>
    public static double? Load(CpuTimes a, CpuTimes b)
    {
        var total = (double)((b.Kernel - a.Kernel) + (b.User - a.User));
        if (total <= 0) return null;
        var idle = (double)(b.Idle - a.Idle);
        return Math.Clamp(1 - idle / total, 0, 1);
    }
}

public readonly record struct MemoryReading(ulong UsedBytes, ulong TotalBytes);

/// <summary>Utilisation 0..1, memory used fraction 0..1, temperature in whole degrees C.</summary>
public readonly record struct GpuReading(double Utilization, double MemoryFraction, double TemperatureC);

/// <summary>One native reader per metric. Any method may return null when the reading is not
/// available this time; the source skips that sample. Implementations must not throw.</summary>
public interface IHardwareReader
{
    CpuTimes? ReadCpu();
    MemoryReading? ReadMemory();
    GpuReading? ReadGpu();
    /// <summary>False when no GPU reader could be set up at all (no NVML). The source then
    /// publishes no gpu* fields rather than fields that are always missing.</summary>
    bool HasGpu { get; }
}
```

```csharp
// tests/DeskWall.Core.Tests/Sources/HardwareSourceTests.cs
using DeskWall.Core.Sources.Hardware;
using DeskWall.Core.Values;
using Xunit;

public class HardwareSourceTests
{
    private sealed class FakeReader : IHardwareReader
    {
        public Queue<CpuTimes?> Cpu = new();
        public Queue<MemoryReading?> Mem = new();
        public Queue<GpuReading?> Gpu = new();
        public bool HasGpu { get; set; } = true;
        public CpuTimes? ReadCpu() => Cpu.Count > 0 ? Cpu.Dequeue() : null;
        public MemoryReading? ReadMemory() => Mem.Count > 0 ? Mem.Dequeue() : null;
        public GpuReading? ReadGpu() => Gpu.Count > 0 ? Gpu.Dequeue() : null;
    }

    private static HardwareSource Make(FakeReader r, int windowSamples = 30)
        => new("hw", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10 * windowSamples), r, autoStart: false);

    private static double N(RecordValue rec, string field) => ((NumberValue)rec.Fields[field]).Number;

    [Fact]
    public void Cpu_Load_Needs_Two_Readings_Then_Averages()
    {
        var r = new FakeReader();
        // 100 ticks elapsed each step; idle 50 then 20 -> loads 0.5, 0.8
        r.Cpu.Enqueue(new CpuTimes(0, 0, 0));
        r.Cpu.Enqueue(new CpuTimes(50, 60, 40));
        r.Cpu.Enqueue(new CpuTimes(70, 120, 80));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce(); s.SampleOnce();
        var rec = s.RefreshAsync(default).Result;
        Assert.Equal(2, N(rec, "samples"));
        Assert.Equal(0.65, N(rec, "cpu"), 3);
        Assert.Equal(65, N(rec, "cpuPct"));
        Assert.Equal(0.8, N(rec, "cpuNow"), 3);
    }

    [Fact]
    public void Memory_Publishes_Fraction_And_GB()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(8UL << 30, 16UL << 30));
        var s = Make(r);
        s.SampleOnce();
        var rec = s.RefreshAsync(default).Result;
        Assert.Equal(0.5, N(rec, "ram"), 3);
        Assert.Equal(50, N(rec, "ramPct"));
        Assert.Equal(8.6, N(rec, "ramUsedGB"), 1);     // 8 GiB = 8.6 GB (decimal), one decimal
        Assert.Equal(17.2, N(rec, "ramTotalGB"), 1);
    }

    [Fact]
    public void Gpu_Publishes_Utilisation_Memory_And_Temperature()
    {
        var r = new FakeReader();
        r.Gpu.Enqueue(new GpuReading(0.10, 0.20, 50));
        r.Gpu.Enqueue(new GpuReading(0.30, 0.40, 54));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce();
        var rec = s.RefreshAsync(default).Result;
        Assert.Equal(0.2, N(rec, "gpu"), 3);
        Assert.Equal(20, N(rec, "gpuPct"));
        Assert.Equal(0.3, N(rec, "gpuNow"), 3);
        Assert.Equal(0.3, N(rec, "gpuMemory"), 3);
        Assert.Equal(52, N(rec, "gpuTempC"));
        Assert.Equal(0.52, N(rec, "gpuTempFraction"), 3);
    }

    [Fact]
    public void No_Gpu_Means_No_Gpu_Fields()
    {
        var r = new FakeReader { HasGpu = false };
        r.Mem.Enqueue(new MemoryReading(1, 2));
        var s = Make(r);
        s.SampleOnce();
        var rec = s.RefreshAsync(default).Result;
        Assert.False(rec.Fields.ContainsKey("gpu"));
        Assert.False(rec.Fields.ContainsKey("gpuTempC"));
        Assert.True(rec.Fields.ContainsKey("ram"));
    }

    [Fact]
    public void Zero_Samples_Publishes_Only_Samples_And_Window()
    {
        var s = Make(new FakeReader());
        var rec = s.RefreshAsync(default).Result;
        Assert.Equal(0, N(rec, "samples"));
        Assert.Equal(300, N(rec, "window"));
        Assert.Equal(2, rec.Fields.Count);
    }

    [Fact]
    public void Fractions_Are_Rounded_To_Three_Decimals()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(1, 3));
        var s = Make(r);
        s.SampleOnce();
        var rec = s.RefreshAsync(default).Result;
        Assert.Equal(0.333, N(rec, "ram"));
    }

    [Fact]
    public void Failed_Reading_Is_Skipped_Not_Counted()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(null);
        r.Mem.Enqueue(new MemoryReading(1, 4));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce();
        var rec = s.RefreshAsync(default).Result;
        Assert.Equal(0.25, N(rec, "ram"), 3);
    }

    [Fact]
    public void Samples_Reports_The_Largest_Window_Count()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(1, 4)); r.Mem.Enqueue(new MemoryReading(1, 4));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce();
        Assert.Equal(2, N(s.RefreshAsync(default).Result, "samples"));
    }
}
```

- [ ] **Step 6: Run HardwareSourceTests, expect compile failure (`HardwareSource` missing)**

- [ ] **Step 7: Implement HardwareSource**

```csharp
// src/DeskWall.Core/Sources/Hardware/HardwareSource.cs
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources.Hardware;

/// <summary>every default 60, sample default 10, window default 300 (seconds). A timer inside the
/// source samples cpu/ram/gpu every `sample` seconds into rings of window/sample slots; RefreshAsync
/// publishes the averages. The daemon's scheduler only ever sees `every`, so the daemon still wakes
/// once a minute (spec: sample 10 s, paint on the minute).
/// Publishes: cpu, cpuPct, cpuNow, ram, ramPct, ramUsedGB, ramTotalGB, samples, window, and when a
/// GPU reader exists gpu, gpuPct, gpuNow, gpuMemory, gpuTempC, gpuTempFraction.</summary>
public sealed class HardwareSource : PeriodicSource, IDisposable
{
    private readonly IHardwareReader _reader;
    private readonly TimeSpan _sample;
    private readonly TimeSpan _window;
    private readonly object _lock = new();
    private readonly RollingWindow _cpu, _ram, _gpu, _gpuMem, _gpuTemp;
    private CpuTimes? _lastCpu;
    private MemoryReading? _lastMem;
    private Timer? _timer;
    private readonly bool _autoStart;

    public HardwareSource(string name, TimeSpan every, TimeSpan sample, TimeSpan window, IHardwareReader reader, bool autoStart = true)
        : base(name, every)
    {
        _reader = reader;
        _sample = sample <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : sample;
        _window = window <= TimeSpan.Zero ? TimeSpan.FromSeconds(300) : window;
        var slots = (int)Math.Max(1, Math.Round(_window.TotalSeconds / _sample.TotalSeconds));
        _cpu = new(slots); _ram = new(slots); _gpu = new(slots); _gpuMem = new(slots); _gpuTemp = new(slots);
        _autoStart = autoStart;
    }

    public static HardwareSource FromDef(SourceDef def)
    {
        var s = def.Settings;
        var every = TimeSpan.FromSeconds(def.EverySeconds ?? 60);
        var sample = TimeSpan.FromSeconds(Seconds(s, "sample", 10));
        var window = TimeSpan.FromSeconds(Seconds(s, "window", 300));
        return new HardwareSource(def.Name, every, sample, window, new Win32HardwareReader());
    }

    private static double Seconds(Dictionary<string, string> s, string key, double fallback)
        => s.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : fallback;

    /// <summary>Take one reading of every metric. Called by the timer; public so tests drive it.</summary>
    public void SampleOnce()
    {
        lock (_lock)
        {
            var cpu = _reader.ReadCpu();
            if (cpu is { } c)
            {
                if (_lastCpu is { } prev && CpuTimes.Load(prev, c) is { } load) _cpu.Add(load);
                _lastCpu = c;
            }
            var mem = _reader.ReadMemory();
            if (mem is { } m && m.TotalBytes > 0)
            {
                _ram.Add((double)m.UsedBytes / m.TotalBytes);
                _lastMem = m;
            }
            if (_reader.HasGpu && _reader.ReadGpu() is { } g)
            {
                _gpu.Add(Math.Clamp(g.Utilization, 0, 1));
                _gpuMem.Add(Math.Clamp(g.MemoryFraction, 0, 1));
                _gpuTemp.Add(g.TemperatureC);
            }
        }
    }

    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        if (_autoStart && _timer is null) _timer = new Timer(_ => SampleOnce(), null, _sample, _sample);
        lock (_lock)
        {
            var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
            {
                ["samples"] = new NumberValue(Math.Max(Math.Max(_cpu.Count, _ram.Count), _gpu.Count)),
                ["window"] = new NumberValue(_window.TotalSeconds),
            };
            if (_cpu.Average is { } cpu)
            {
                d["cpu"] = new NumberValue(Math.Round(cpu, 3));
                d["cpuPct"] = new NumberValue(Math.Round(cpu * 100));
                d["cpuNow"] = new NumberValue(Math.Round(_cpu.Latest!.Value, 3));
            }
            if (_ram.Average is { } ram)
            {
                d["ram"] = new NumberValue(Math.Round(ram, 3));
                d["ramPct"] = new NumberValue(Math.Round(ram * 100));
                if (_lastMem is { } m)
                {
                    d["ramUsedGB"] = new NumberValue(Math.Round(m.UsedBytes / 1e9, 1));
                    d["ramTotalGB"] = new NumberValue(Math.Round(m.TotalBytes / 1e9, 1));
                }
            }
            if (_reader.HasGpu && _gpu.Average is { } gpu)
            {
                d["gpu"] = new NumberValue(Math.Round(gpu, 3));
                d["gpuPct"] = new NumberValue(Math.Round(gpu * 100));
                d["gpuNow"] = new NumberValue(Math.Round(_gpu.Latest!.Value, 3));
                d["gpuMemory"] = new NumberValue(Math.Round(_gpuMem.Average ?? 0, 3));
                var t = Math.Round(_gpuTemp.Average ?? 0);
                d["gpuTempC"] = new NumberValue(t);
                d["gpuTempFraction"] = new NumberValue(Math.Round(t / 100, 3));
            }
            return new(new RecordValue(d));
        }
    }

    public void Dispose() { _timer?.Dispose(); _timer = null; }
}
```

- [ ] **Step 8: Run HardwareSourceTests, expect 8 passed**

- [ ] **Step 9: Native readers.** Append to `src/DeskWall.Core/NativeMethods.txt`:

```
GetSystemTimes
GlobalMemoryStatusEx
MEMORYSTATUSEX
FILETIME
```

```csharp
// src/DeskWall.Core/Sources/Hardware/Win32HardwareReader.cs
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;

namespace DeskWall.Core.Sources.Hardware;

/// <summary>GetSystemTimes for CPU, GlobalMemoryStatusEx for RAM, NVML for the GPU. Every call is
/// microseconds and allocation-free; none throws (a failed native call returns null).</summary>
public sealed unsafe class Win32HardwareReader : IHardwareReader
{
    private readonly NvmlGpuReader? _gpu = NvmlGpuReader.TryCreate();

    public bool HasGpu => _gpu is not null;

    public CpuTimes? ReadCpu()
    {
        FILETIME idle, kernel, user;
        if (!PInvoke.GetSystemTimes(&idle, &kernel, &user)) return null;
        return new CpuTimes(ToUlong(idle), ToUlong(kernel), ToUlong(user));
    }

    private static ulong ToUlong(FILETIME t) => ((ulong)t.dwHighDateTime << 32) | t.dwLowDateTime;

    public MemoryReading? ReadMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!PInvoke.GlobalMemoryStatusEx(&ms)) return null;
        return new MemoryReading(ms.ullTotalPhys - ms.ullAvailPhys, ms.ullTotalPhys);
    }

    public GpuReading? ReadGpu() => _gpu?.Read();
}
```

If CsWin32 generates `GetSystemTimes` with `out FILETIME` parameters instead of pointers, adapt to what it generated and record the shape in the spike results doc.

```csharp
// src/DeskWall.Core/Sources/Hardware/NvmlGpuReader.cs
using System.Runtime.InteropServices;

namespace DeskWall.Core.Sources.Hardware;

/// <summary>NVIDIA Management Library, loaded from the driver's copy in System32 (fallback: the
/// legacy NVSMI folder). Absent library, failed init, or no device all mean TryCreate returns null
/// and the hardware source publishes no gpu fields. Every entry point is cdecl and returns an
/// nvmlReturn_t where 0 is success. Called through unmanaged function pointers: no marshalling,
/// native-AOT safe.</summary>
public sealed unsafe class NvmlGpuReader : IDisposable
{
    private readonly IntPtr _lib;
    private readonly IntPtr _device;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> _getUtil;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Memory*, int> _getMem;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _getTemp;
    private readonly delegate* unmanaged[Cdecl]<int> _shutdown;

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    private NvmlGpuReader(IntPtr lib, IntPtr device,
        delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> getUtil,
        delegate* unmanaged[Cdecl]<IntPtr, Memory*, int> getMem,
        delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> getTemp,
        delegate* unmanaged[Cdecl]<int> shutdown)
    { _lib = lib; _device = device; _getUtil = getUtil; _getMem = getMem; _getTemp = getTemp; _shutdown = shutdown; }

    public static NvmlGpuReader? TryCreate()
    {
        IntPtr lib = IntPtr.Zero;
        foreach (var path in Candidates())
            if (NativeLibrary.TryLoad(path, out lib)) break;
        if (lib == IntPtr.Zero) return null;
        try
        {
            var init = (delegate* unmanaged[Cdecl]<int>)Export(lib, "nvmlInit_v2");
            var byIndex = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)Export(lib, "nvmlDeviceGetHandleByIndex_v2");
            var getUtil = (delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int>)Export(lib, "nvmlDeviceGetUtilizationRates");
            var getMem = (delegate* unmanaged[Cdecl]<IntPtr, Memory*, int>)Export(lib, "nvmlDeviceGetMemoryInfo");
            var getTemp = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Export(lib, "nvmlDeviceGetTemperature");
            var shutdown = (delegate* unmanaged[Cdecl]<int>)Export(lib, "nvmlShutdown");
            if (init == null || byIndex == null || getUtil == null || getMem == null || getTemp == null || shutdown == null) { NativeLibrary.Free(lib); return null; }
            if (init() != 0) { NativeLibrary.Free(lib); return null; }
            IntPtr device;
            if (byIndex(0, &device) != 0) { shutdown(); NativeLibrary.Free(lib); return null; }
            return new NvmlGpuReader(lib, device, getUtil, getMem, getTemp, shutdown);
        }
        catch (Exception) { NativeLibrary.Free(lib); return null; }
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
    }

    private static void* Export(IntPtr lib, string name)
        => NativeLibrary.TryGetExport(lib, name, out var p) ? (void*)p : null;

    public GpuReading? Read()
    {
        Utilization u; Memory m; uint t;
        if (_getUtil(_device, &u) != 0) return null;
        if (_getMem(_device, &m) != 0) return null;
        if (_getTemp(_device, 0 /* NVML_TEMPERATURE_GPU */, &t) != 0) return null;
        var memFrac = m.Total == 0 ? 0 : (double)m.Used / m.Total;
        return new GpuReading(u.Gpu / 100.0, memFrac, t);
    }

    public void Dispose() { _shutdown(); NativeLibrary.Free(_lib); }
}
```

Note `nvmlDeviceGetMemoryInfo` (v1) fills a 24-byte struct `{total, free, used}`; do not use `_v2`, whose struct differs. `NVML_TEMPERATURE_GPU` is 0.

- [ ] **Step 10: SourceFactory registration + test**

In `SourceFactory.Create` add `"hardware" => Hardware.HardwareSource.FromDef(def),` and in `DefaultEvery` add `"hardware" => 60,`. In `tests/DeskWall.Core.Tests/Sources/SourceFactoryTests.cs` add, following the file's existing pattern for another type:

```csharp
[Fact]
public void Hardware_Type_Creates_HardwareSource_With_Defaults()
{
    var def = new SourceDef { Name = "hw", Type = "hardware" };
    var s = SourceFactory.Create(def, new FakeClock(DateTimeOffset.UnixEpoch));
    var hw = Assert.IsType<DeskWall.Core.Sources.Hardware.HardwareSource>(s);
    Assert.Equal(TimeSpan.FromSeconds(60), hw.Every);
}
```

(Use whatever clock fake the file already uses.) Run: `dotnet test tests/DeskWall.Core.Tests --filter FullyQualifiedName~SourceFactoryTests`. Expected: pass.

- [ ] **Step 11: Live sanity on JOES-PC, scratch home only.** Create `scratch/hw.json` in your `DESKWALL_HOME` scratch dir (a copy of `layouts/clock-disks.json` with `{ "name": "hardware", "type": "hardware" }` added to `sources` and one text component `{ "type": "text", "id": "cpu", "rect": [3220, 600, 172, 30], "text": { "bind": "hardware.cpuPct | \"cpu {0}%  gpu \"" } }` — or simpler, bind `hardware.samples`). Build the daemon (`dotnet build src/DeskWall.Daemon -c Release`) and run:

```powershell
$exe = "src\DeskWall.Daemon\bin\Release\net10.0-windows10.0.19041.0\win-x64\deskwall.exe"
Start-Process $exe -ArgumentList @('--home', "`"$env:TEMP\dw-hw`"", 'tick', '--layout', '"<scratch>\hw.json"', '--force', '--measure', '--no-apply', '--no-shortcuts') -NoNewWindow -Wait -RedirectStandardOutput "$env:TEMP\hw.out"
```

A one-shot tick has zero samples (the timer has not fired). Verify instead with a short resident run against the scratch home for 90 s (`run --no-tray --no-shortcuts`, kill it after), then `deskwall --home <scratch> tick --measure --no-apply --no-shortcuts` is still one-shot; so instead add a temporary `Console.WriteLine` in nothing: use the daemon log and a `text` bound to `hardware.samples | "samples {0}"` and read the value from the rendered `deskwall.jpg` in the scratch home (open it). Report what the GPU reader returned on this machine (RTX 3050, `nvml.dll` present in System32).

- [ ] **Step 12: Build clean, all Core tests, docs, commit**

`dotnet build` → 0 warnings. `dotnet test tests/DeskWall.Core.Tests` → all pass. Add the `## \`hardware\`` section to `docs/sources.md` (settings, published table from the spec, the sampler note, the NVML note, the no-CPU-temperature ruling in one line). Commit:

```
git add -A src/DeskWall.Core tests/DeskWall.Core.Tests docs/sources.md
git commit -m "Sources: hardware (cpu, ram, nvml gpu) sampled every 10 s, 5-minute averages"
```

---

### Task 2: `dial` component

**Files:**
- Modify: `src/DeskWall.Core/Layout/ComponentDef.cs` (add `[JsonDerivedType(typeof(DialDef), "dial")]` and `DialDef`)
- Modify: `src/DeskWall.Core/Resolve/Resolved.cs` (add `ResolvedDial`)
- Modify: `src/DeskWall.Core/Resolve/LayoutResolver.cs` (add `case DialDef`)
- Modify: `src/DeskWall.Core/Layout/LayoutScaler.cs` (scale `Thickness`)
- Modify: `src/DeskWall.Core/Render/Surface.cs` (add `DrawArc`)
- Modify: `src/DeskWall.Core/Render/FrameRenderer.cs` (add `case ResolvedDial`)
- Modify: `src/DeskWall.Core/NativeMethods.txt` (append `ID2D1PathGeometry`, `ID2D1GeometrySink`, `ID2D1SimplifiedGeometrySink`, `D2D1_ARC_SEGMENT`, `D2D1_SWEEP_DIRECTION`, `D2D1_ARC_SIZE`, `D2D1_FIGURE_BEGIN`, `D2D1_FIGURE_END`, `D2D_SIZE_F`, `D2D_POINT_2F`)
- Test: `tests/DeskWall.Core.Tests/Resolve/LayoutResolverTests.cs`, `tests/DeskWall.Core.Tests/Resolve/ResolvedTests.cs`, `tests/DeskWall.Core.Tests/Layout/LayoutScalerTests.cs`, `tests/DeskWall.Core.Tests/Goldens/GoldenTests.cs` + `Goldens/layouts/dial-states.json` + `Goldens/dial-states.png`
- Docs: `docs/layout-format.md` (`### \`dial\`` after `bar`), spike results doc (new shapes)

**Interfaces:**
- Produces: `DialDef { Fraction (required), Track, Fill, Threshold, ThresholdFill, Thickness, StartAngle, Sweep }` all `PropertyValue`; `ResolvedDial(string Id, Rect Rect, int Z, double Fraction, Color Track, Color Fill, float Thickness, float StartAngle, float Sweep)`; `Surface.DrawArc(Rect r, float startDeg, float sweepDeg, float thickness, Color c)`. Task 5 reads `DialDef` property names verbatim.
- Consumes: `PropertyReader.Number/Color`, `Color.Parse`, `Resolved.KeyParts`, the `Draw(rt => ...)` helper and `Brush(rt, c)` in `Surface`.

- [ ] **Step 1: Resolver failing tests.** Add to `LayoutResolverTests.cs` (match the file's helpers for building a `LayoutFile` / scope; if it parses JSON strings, do the same):

```csharp
[Fact]
public void Dial_Resolves_Defaults_And_Clamps()
{
    var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "dial", "id": "d", "rect": [10, 10, 80, 80], "fraction": 1.7 } ] }
        """);
    var d = Assert.IsType<ResolvedDial>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
    Assert.Equal(1.0, d.Fraction);
    Assert.Equal(Color.Parse("#46FFFFFF"), d.Track);
    Assert.Equal(Color.Parse("#EBFFFFFF"), d.Fill);
    Assert.Equal(6f, d.Thickness);
    Assert.Equal(135f, d.StartAngle);
    Assert.Equal(270f, d.Sweep);
}

[Fact]
public void Dial_At_Or_Above_Threshold_Uses_ThresholdFill()
{
    var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "dial", "id": "d", "rect": [0, 0, 50, 50], "fraction": 0.9, "threshold": 0.9, "thresholdFill": "#FF112233" } ] }
        """);
    var d = Assert.IsType<ResolvedDial>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
    Assert.Equal(Color.Parse("#FF112233"), d.Fill);
}

[Fact]
public void Dial_Bound_Fraction_Missing_Falls_Back_To_Zero()
{
    var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "dial", "id": "d", "rect": [0, 0, 50, 50], "fraction": { "bind": "hw.cpu" } } ] }
        """);
    var d = Assert.IsType<ResolvedDial>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
    Assert.Equal(0.0, d.Fraction);
}
```

If `LayoutFile.Parse(string)` does not exist, use whatever the file already uses to load a layout from a JSON string (there is a helper in the existing tests; do not add a new public API for this).

Add to `ResolvedTests.cs`:

```csharp
[Fact]
public void Dial_Key_Quantises_Fraction_To_Three_Decimals()
{
    var a = new ResolvedDial("d", new Rect(0, 0, 80, 80), 0, 0.5001, Color.Parse("#46FFFFFF"), Color.Parse("#EBFFFFFF"), 6, 135, 270);
    var b = a with { Fraction = 0.5004 };
    var c = a with { Fraction = 0.501 };
    Assert.Equal(string.Join("|", a.KeyParts()), string.Join("|", b.KeyParts()));
    Assert.NotEqual(string.Join("|", a.KeyParts()), string.Join("|", c.KeyParts()));
}

[Fact]
public void Dial_Paint_Bounds_Is_Its_Rect()
{
    var d = new ResolvedDial("d", new Rect(5, 6, 80, 80), 0, 0.5, Color.Parse("#46FFFFFF"), Color.Parse("#EBFFFFFF"), 6, 135, 270);
    Assert.Equal(d.Rect, d.PaintBounds);
}
```

- [ ] **Step 2: Run, expect compile failures (`DialDef`, `ResolvedDial` missing)**

- [ ] **Step 3: DialDef, ResolvedDial, resolver case**

`ComponentDef.cs`: add `[JsonDerivedType(typeof(DialDef), "dial")]` under the `bar` line and:

```csharp
public sealed class DialDef : ComponentDef
{
    public required PropertyValue Fraction { get; set; }
    public PropertyValue Track { get; set; } = PropertyValue.Literal("#46FFFFFF");
    public PropertyValue Fill { get; set; } = PropertyValue.Literal("#EBFFFFFF");
    public PropertyValue Threshold { get; set; } = PropertyValue.Literal(1);
    public PropertyValue ThresholdFill { get; set; } = PropertyValue.Literal("#D13438");
    public PropertyValue Thickness { get; set; } = PropertyValue.Literal(6);
    public PropertyValue StartAngle { get; set; } = PropertyValue.Literal(135);
    public PropertyValue Sweep { get; set; } = PropertyValue.Literal(270);
}
```

`Resolved.cs`:

```csharp
/// <summary>A thin arc: Track over the full Sweep, Fill over Sweep * Fraction, both Thickness px
/// wide and centred in Rect. Nothing else is drawn; a number inside is a text component.</summary>
public sealed record ResolvedDial(string Id, Rect Rect, int Z, double Fraction, Color Track, Color Fill, float Thickness, float StartAngle, float Sweep) : Resolved(Id, Rect, Z)
{
    // Same quantisation as ResolvedBar: a 5-minute average moves below a pixel between reads.
    public override IEnumerable<string> KeyParts() =>
        [Math.Round(Fraction, 3).ToString("R"), Track.ToHex(), Fill.ToHex(), Thickness.ToString("R"), StartAngle.ToString("R"), Sweep.ToString("R")];
}
```

`LayoutResolver.cs`, after `case BarDef b:` block:

```csharp
case DialDef dl:
    var dfrac = Math.Clamp(PropertyReader.Number(dl.Fraction, scope) ?? 0, 0, 1);
    var dthr = PropertyReader.Number(dl.Threshold, scope) ?? 1;
    var dfill = dfrac >= dthr
        ? PropertyReader.Color(dl.ThresholdFill, scope) ?? Color.Parse("#FFD13438")
        : PropertyReader.Color(dl.Fill, scope) ?? Color.Parse("#EBFFFFFF");
    result.Add(new ResolvedDial(id, rect, def.Z, dfrac,
        PropertyReader.Color(dl.Track, scope) ?? Color.Parse("#46FFFFFF"), dfill,
        (float)(PropertyReader.Number(dl.Thickness, scope) ?? 6),
        (float)(PropertyReader.Number(dl.StartAngle, scope) ?? 135),
        (float)(PropertyReader.Number(dl.Sweep, scope) ?? 270)));
    break;
```

Check `Color.Parse("#D13438")` (6-digit) is accepted by `Color.Parse`; `BarDef` uses the same literal so it is.

- [ ] **Step 4: Run resolver + resolved tests, expect pass**

- [ ] **Step 5: Scaler test + change.** In `LayoutScalerTests.cs`, following the existing test for `bar` or `text` size scaling:

```csharp
[Fact]
public void Dial_Thickness_Scales_With_The_Smaller_Factor()
{
    var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "dial", "id": "d", "rect": [0, 0, 100, 100], "fraction": 0.5, "thickness": 10 } ] }
        """);
    var scaled = LayoutScaler.Scale(layout, /* from */ Sig(1000, 1000), /* to */ Sig(500, 2000));
    var d = Assert.IsType<DialDef>(Assert.Single(scaled.Components));
    Assert.Equal("5", d.Thickness.LiteralText);
}
```

(Use the file's existing signature helper in place of `Sig`.) In `LayoutScaler.ScaleComponent` add `case DialDef dl: ScaleLiteral(dl, nameof(DialDef.Thickness), sm); break;` and in `ScaleLiteral` add `case (DialDef dl, nameof(DialDef.Thickness)): dl.Thickness = ScaleNumber(dl.Thickness, factor); break;`. Run, expect pass.

- [ ] **Step 6: DrawArc.** Append the declarations listed in Files to `NativeMethods.txt`, build once so CsWin32 generates them, then add to `Surface.cs` (next to `FillRect`):

```csharp
/// <summary>Stroke an arc of a circle centred in <paramref name="r"/>, radius min(w,h)/2 -
/// thickness/2, from <paramref name="startDeg"/> (clockwise from 12 o'clock) through
/// <paramref name="sweepDeg"/> degrees clockwise. Flat caps. A sweep of 360 or more is drawn as two
/// half circles because a single D2D arc segment cannot describe a full turn.</summary>
public void DrawArc(Rect r, float startDeg, float sweepDeg, float thickness, Color c) => Draw(rt =>
{
    if (sweepDeg <= 0 || thickness <= 0) return;
    var radius = Math.Min(r.W, r.H) / 2f - thickness / 2f;
    if (radius <= 0) return;
    var cx = r.X + r.W / 2f; var cy = r.Y + r.H / 2f;
    var brush = Brush(rt, c);
    ID2D1PathGeometry* geo = null; ID2D1GeometrySink* sink = null;
    try
    {
        s_d2d->CreatePathGeometry(&geo);
        geo->Open(&sink);
        var segments = sweepDeg >= 360 ? 2 : 1;
        var per = Math.Min(sweepDeg, 360) / segments;
        sink->BeginFigure(Point(cx, cy, radius, startDeg), D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);
        for (var i = 1; i <= segments; i++)
        {
            var arc = new D2D1_ARC_SEGMENT
            {
                point = Point(cx, cy, radius, startDeg + per * i),
                size = new D2D_SIZE_F { width = radius, height = radius },
                rotationAngle = 0,
                sweepDirection = D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_CLOCKWISE,
                arcSize = per > 180 ? D2D1_ARC_SIZE.D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE.D2D1_ARC_SIZE_SMALL,
            };
            sink->AddArc(&arc);
        }
        sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
        sink->Close();
        rt->DrawGeometry((ID2D1Geometry*)geo, (ID2D1Brush*)brush, thickness, null);
    }
    finally { if (sink is not null) sink->Release(); if (geo is not null) geo->Release(); brush->Release(); }
});

private static D2D_POINT_2F Point(float cx, float cy, float radius, float deg)
{
    var rad = (deg - 90) * Math.PI / 180;   // 0 deg = 12 o'clock, clockwise
    return new D2D_POINT_2F { x = cx + radius * (float)Math.Cos(rad), y = cy + radius * (float)Math.Sin(rad) };
}
```

`AddArc` lives on `ID2D1GeometrySink`; `BeginFigure/EndFigure/Close` on its base `ID2D1SimplifiedGeometrySink`. With `allowMarshaling: false` CsWin32 generates base-interface methods on the derived struct, so calls on `sink->` work; if a method is missing, cast `(ID2D1SimplifiedGeometrySink*)sink`. Record the shapes that compiled in the spike results doc under "CsWin32 shapes verified".

- [ ] **Step 7: Renderer case.** In `FrameRenderer.Draw`, after the `ResolvedBar` case:

```csharp
case ResolvedDial d:
    frame.DrawArc(d.Rect, d.StartAngle, d.Sweep, d.Thickness, d.Track);
    var sweep = (float)(d.Sweep * Math.Clamp(d.Fraction, 0, 1));
    if (sweep > 0) frame.DrawArc(d.Rect, d.StartAngle, sweep, d.Thickness, d.Fill);
    break;
```

- [ ] **Step 8: Golden.** Create `tests/DeskWall.Core.Tests/Goldens/layouts/dial-states.json`:

```json
{
  "version": 1,
  "baseImage": "unused.jpg",
  "sources": [],
  "components": [
    { "type": "dial", "id": "zero",      "rect": [40, 40, 120, 120],  "fraction": 0 },
    { "type": "dial", "id": "half",      "rect": [200, 40, 120, 120], "fraction": 0.5 },
    { "type": "dial", "id": "full",      "rect": [360, 40, 120, 120], "fraction": 1 },
    { "type": "dial", "id": "threshold", "rect": [520, 40, 120, 120], "fraction": 0.9, "threshold": 0.85 },
    { "type": "dial", "id": "thick",     "rect": [680, 40, 120, 120], "fraction": 0.7, "thickness": 12 },
    { "type": "dial", "id": "topsweep",  "rect": [40, 200, 120, 120], "fraction": 0.5, "startAngle": 0, "sweep": 360 },
    { "type": "dial", "id": "small",     "rect": [200, 200, 40, 40],  "fraction": 0.75, "thickness": 4 },
    { "type": "dial", "id": "wide",      "rect": [360, 200, 200, 100], "fraction": 0.6 }
  ]
}
```

Add to `GoldenTests.cs`:

```csharp
[Fact]
public void Dial_States()
{
    // Zero, half, full, over threshold (fill colour switches), 12 px thick, a full 360 sweep from
    // 12 o'clock, a 40 px dial, and a non-square rect (arc centred, radius from the short side).
    RunGolden("dial-states", ValueTree.Empty);
}
```

Run once with `GOLDENS_UPDATE=1` (PowerShell: `$env:GOLDENS_UPDATE='1'; dotnet test tests/DeskWall.Core.Tests --filter FullyQualifiedName~GoldenTests.Dial_States; Remove-Item Env:GOLDENS_UPDATE`), copy the produced `dial-states.png` from the test output's `Goldens` folder into `tests/DeskWall.Core.Tests/Goldens/`, **open it and look at it** (eight arcs, threshold one red, the 360 one a half circle from the top). Then run the golden test again without the variable: pass. Make sure the csproj copies `Goldens/**` to output the way the other goldens are copied (check `DeskWall.Core.Tests.csproj`; if it globs `Goldens\**\*` nothing to do).

- [ ] **Step 9: Docs.** `docs/layout-format.md`: add `### \`dial\`` after `### \`bar\`` with the property table from the spec section 4 and one sentence on the angle convention. Spike results: new shapes under "CsWin32 shapes verified".

- [ ] **Step 10: Build clean, all Core tests, commit**

```
git add -A src/DeskWall.Core tests/DeskWall.Core.Tests docs
git commit -m "Render: dial component (thin arc, threshold fill) with golden"
```

---

### Task 3: `runtime:` image path prefix (same lane as Task 2)

**Files:**
- Modify: `src/DeskWall.Core/Resolve/LayoutResolver.cs` (ImageDef case)
- Test: `tests/DeskWall.Core.Tests/Resolve/LayoutResolverTests.cs`
- Docs: `docs/layout-format.md` (`image.source` row)

**Interfaces:**
- Produces: an `image.source` text beginning with `runtime:` (case-insensitive) resolves to `Paths.InRuntime(<rest with '/' normalised to '\'>)` after binding/format. Task 4's layout uses `runtime:assets/weather/{0}.png`.

- [ ] **Step 1: Failing test**

```csharp
[Fact]
public void Image_Source_Runtime_Prefix_Resolves_Into_The_Runtime_Dir()
{
    var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "image", "id": "i", "rect": [0, 0, 10, 10],
                            "source": { "bind": "w.code | \"runtime:assets/weather/{0}.png\"" } } ] }
        """);
    var tree = ValueTree.Of(("w", new RecordValue(new Dictionary<string, Value> { ["code"] = new NumberValue(61) })));
    var img = Assert.IsType<ResolvedImage>(Assert.Single(LayoutResolver.Resolve(layout, tree)));
    Assert.Equal(Paths.InRuntime("assets", "weather", "61.png"), img.Path);
}

[Fact]
public void Image_Source_Without_Prefix_Is_Unchanged()
{
    var layout = LayoutFile.Parse("""
        { "version": 1, "baseImage": "x.jpg", "sources": [],
          "components": [ { "type": "image", "id": "i", "rect": [0, 0, 10, 10], "source": "C:\\pics\\a.png" } ] }
        """);
    var img = Assert.IsType<ResolvedImage>(Assert.Single(LayoutResolver.Resolve(layout, ValueTree.Empty)));
    Assert.Equal(@"C:\pics\a.png", img.Path);
}
```

- [ ] **Step 2: Run, expect the first to fail (path still has the prefix)**

- [ ] **Step 3: Implement.** In the `case ImageDef i:` replace `PropertyReader.Text(i.Source, scope) ?? ""` with `ExpandRuntime(PropertyReader.Text(i.Source, scope) ?? "")` and add:

```csharp
/// <summary>"runtime:assets/weather/61.png" -> %LOCALAPPDATA%\DeskWall\assets\weather\61.png. Lets a
/// committed starter name a per-user file without a per-user absolute path. Braces are not used
/// for the token because a composite format string would swallow them.</summary>
private static string ExpandRuntime(string source)
{
    const string prefix = "runtime:";
    if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return source;
    var rest = source[prefix.Length..].Replace('/', '\\').TrimStart('\\');
    return Paths.InRuntime(rest.Split('\\', StringSplitOptions.RemoveEmptyEntries));
}
```

- [ ] **Step 4: Run, expect pass; docs; commit**

`docs/layout-format.md` `image` table, `source` row: append "A local path may start with `runtime:` to be resolved under the runtime dir (`runtime:assets/weather/{0}.png`)." Commit: `Resolve: runtime: prefix on image sources`.

---

### Task 4: Weather icons, starter layout, recipes docs

**Files:**
- Create: `assets/weather/<code>.png` for codes 0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99; `assets/weather/LICENSE`; `assets/weather/README.md`
- Create: `layouts/column-system.json`
- Test: `tests/DeskWall.Core.Tests/Layout/StarterLayoutTests.cs`
- Modify: `layouts/README.md`, `docs/layout-format.md` (binding examples 11 and 12)

**Interfaces:**
- Consumes: Task 1's field names, Task 2's `dial` properties, Task 3's `runtime:` prefix. This lane can start before those merge: the JSON is data. The test in Step 3 will fail to load `dial` until Task 2 merges; write it, run it, note the expected failure in the report, and the controller re-runs after merge.

- [ ] **Step 1: Icons.** Source: Basmilius Meteocons (MIT), https://github.com/basmilius/weather-icons, `production/fill/svg/` or `production/line/svg/`. Pick the **line** set (single-colour, reads on a photo) unless it is not available as monochrome, in which case fill. Mapping WMO -> file:

| WMO | Meteocons file |
|---|---|
| 0 | clear-day |
| 1 | mostly-clear-day (fallback `partly-cloudy-day`) |
| 2 | partly-cloudy-day |
| 3 | overcast |
| 45, 48 | fog |
| 51, 53, 55 | drizzle |
| 56, 57 | sleet |
| 61, 63, 65 | rain |
| 66, 67 | sleet |
| 71, 73, 75, 77 | snow |
| 80, 81, 82 | rain (82: `extreme-rain` if present) |
| 85, 86 | snow |
| 95 | thunderstorms |
| 96, 99 | thunderstorms-rain |

Rasterise each SVG to a 96x96 PNG with a transparent background. The repo has no SVG tooling; use the Playwright MCP tools available in this session (`browser_navigate` to a `file://` HTML page that places the SVG at 96x96, `browser_take_screenshot` with a clip, or `browser_evaluate` drawing the SVG onto a canvas and returning a data URL you decode to a file). If the icons render dark-on-transparent, invert to white in the same canvas step (`ctx.globalCompositeOperation = 'source-in'` with a white fill) so they read on the photo. Save as `assets/weather/<code>.png`, one file per code (duplicate the file for codes sharing an icon). Copy the upstream MIT `LICENSE` beside them. `assets/weather/README.md`: source URL, licence, the mapping table, "96x96 white on transparent", how to regenerate.

- [ ] **Step 2: Starter layout** `layouts/column-system.json`:

```json
{
  "version": 1,
  "baseImage": "C:\\Windows\\SystemApps\\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\\DesktopSpotlight\\Assets\\Images\\image_3.jpg",
  "baseFit": "cover",
  "encode": "jpeg",
  "jpegQuality": 92,
  "sources": [
    { "name": "time", "type": "time" },
    { "name": "disks", "type": "disks", "every": 300 },
    { "name": "weather", "type": "http", "every": 900, "settings": {
        "url": "https://api.open-meteo.com/v1/forecast?latitude=53.8008&longitude=-1.5491&current=temperature_2m,weather_code,is_day&timezone=Europe%2FLondon" } },
    { "name": "hardware", "type": "hardware" },
    { "name": "tailscale", "type": "command", "every": 300, "settings": {
        "command": "C:\\Program Files\\Tailscale\\tailscale.exe", "args": "status --json", "timeout": 10 } }
  ],
  "components": [
    { "type": "text", "id": "clock", "rect": [3220, 40, 172, 78], "z": 1,
      "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "weight": 300, "align": "right" },

    { "type": "text", "id": "temp", "rect": [3220, 128, 108, 52], "z": 1,
      "text": { "bind": "weather.json.current.temperature_2m | \"{0:N0}°\"" }, "font": "Segoe UI Light", "size": 40, "weight": 300, "align": "right" },
    { "type": "image", "id": "sky", "rect": [3336, 126, 56, 56], "z": 1, "fit": "contain",
      "source": { "bind": "weather.json.current.weather_code | \"runtime:assets/weather/{0}.png\"" } },
    { "type": "text", "id": "vpn", "rect": [3220, 190, 172, 20], "z": 1,
      "text": { "bind": "tailscale.json.BackendState | \"VPN {0}\"" }, "size": 13, "align": "right" },

    { "type": "dial", "id": "cpuDial", "rect": [3220, 1000, 80, 80], "z": 1, "fraction": { "bind": "hardware.cpu" }, "threshold": 0.9 },
    { "type": "text", "id": "cpuPct", "rect": [3220, 1026, 80, 28], "z": 2, "text": { "bind": "hardware.cpuPct | \"{0}%\"" }, "size": 18, "align": "center", "effect": "none" },
    { "type": "text", "id": "cpuLabel", "rect": [3220, 1062, 80, 16], "z": 2, "text": "cpu", "size": 11, "align": "center", "color": "#A0FFFFFF", "effect": "none" },

    { "type": "dial", "id": "gpuDial", "rect": [3312, 1000, 80, 80], "z": 1, "fraction": { "bind": "hardware.gpu" }, "threshold": 0.9 },
    { "type": "text", "id": "gpuPct", "rect": [3312, 1026, 80, 28], "z": 2, "text": { "bind": "hardware.gpuPct | \"{0}%\"" }, "size": 18, "align": "center", "effect": "none" },
    { "type": "text", "id": "gpuLabel", "rect": [3312, 1062, 80, 16], "z": 2, "text": "gpu", "size": 11, "align": "center", "color": "#A0FFFFFF", "effect": "none" },

    { "type": "dial", "id": "ramDial", "rect": [3220, 1096, 80, 80], "z": 1, "fraction": { "bind": "hardware.ram" }, "threshold": 0.9 },
    { "type": "text", "id": "ramPct", "rect": [3220, 1122, 80, 28], "z": 2, "text": { "bind": "hardware.ramPct | \"{0}%\"" }, "size": 18, "align": "center", "effect": "none" },
    { "type": "text", "id": "ramLabel", "rect": [3220, 1158, 80, 16], "z": 2, "text": "ram", "size": 11, "align": "center", "color": "#A0FFFFFF", "effect": "none" },

    { "type": "dial", "id": "tempDial", "rect": [3312, 1096, 80, 80], "z": 1, "fraction": { "bind": "hardware.gpuTempFraction" }, "threshold": 0.83 },
    { "type": "text", "id": "tempC", "rect": [3312, 1122, 80, 28], "z": 2, "text": { "bind": "hardware.gpuTempC | \"{0}°\"" }, "size": 18, "align": "center", "effect": "none" },
    { "type": "text", "id": "tempLabel", "rect": [3312, 1158, 80, 16], "z": 2, "text": "gpu °C", "size": 11, "align": "center", "color": "#A0FFFFFF", "effect": "none" },

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

Doctrine note recorded in the lane report: the spec's Gate 3 table deleted dial captions; the three-letter labels above are 11 px at 63 % alpha and exist because four identical arcs with a number inside are indistinguishable from each other. That is a deviation from the spec's own table and must be stated as one; if the controller or the owner rejects it, delete the three `*Label` components. `"effect": "none"` on the in-dial text: the arc is the plate.

- [ ] **Step 3: Starter test** `tests/DeskWall.Core.Tests/Layout/StarterLayoutTests.cs`:

```csharp
using DeskWall.Core.Layout;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using Xunit;

/// <summary>Every layout file shipped under layouts/ loads, and resolves against an empty tree
/// without throwing (a missing binding is a default, never an exception - spec 3.2).</summary>
public class StarterLayoutTests
{
    private static string RepoLayouts()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return Path.Combine(d.FullName, "layouts");
        throw new InvalidOperationException("repo root not found");
    }

    public static IEnumerable<object[]> Files() =>
        Directory.EnumerateFiles(RepoLayouts(), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Files))]
    public void Shipped_Layout_Loads_And_Resolves_Empty(string file)
    {
        var layout = LayoutFile.Load(Path.Combine(RepoLayouts(), file));
        var resolved = LayoutResolver.Resolve(layout, ValueTree.Empty);
        Assert.NotEmpty(resolved);
    }

    [Fact]
    public void Column_System_Uses_Every_New_Source_And_A_Dial()
    {
        var layout = LayoutFile.Load(Path.Combine(RepoLayouts(), "column-system.json"));
        Assert.Contains(layout.Sources, s => s.Type == "hardware");
        Assert.Contains(layout.Sources, s => s.Type == "http" && s.Name == "weather");
        Assert.Contains(layout.Sources, s => s.Type == "command" && s.Name == "tailscale");
        Assert.Contains(layout.Components, c => c is DialDef);
    }
}
```

Run: `dotnet test tests/DeskWall.Core.Tests --filter FullyQualifiedName~StarterLayoutTests`. Before Task 2 merges the `column-system.json` cases fail on the unknown `dial` type: expected, say so in the report. The other starters must pass now.

- [ ] **Step 4: Docs.** `layouts/README.md`: a `column-system.json` entry (what it shows, that it needs Tailscale installed at the default path and an NVIDIA GPU for the two GPU dials, that the weather is Leeds and how to change the coordinates, that `assets/weather` must be under the runtime dir and the designer copies it). `docs/layout-format.md` binding examples: 11 `weather.json.current.temperature_2m | "{0:N0}°"`, 12 `weather.json.current.weather_code | "runtime:assets/weather/{0}.png"`.

- [ ] **Step 5: Commit**

```
git add assets layouts tests/DeskWall.Core.Tests/Layout/StarterLayoutTests.cs docs/layout-format.md
git commit -m "Layouts: column-system starter (weather, hardware dials, tailscale); weather icon set"
```

---

### Task 5: Designer knows `dial` and ships the icons (after Task 2 merges)

**Files:**
- Modify: `src/DeskWall.Designer/Model/PropertySchema.cs` (`For` switch + `DialProps`)
- Modify: `src/DeskWall.Designer/Views/LayersPanel.xaml.cs` (`TypeName`)
- Modify: `src/DeskWall.Designer/DeskWall.Designer.csproj` (link `..\..\assets\weather\*` into `assets\weather\` output)
- Modify: `src/DeskWall.Designer/Views/FirstRun.xaml.cs` (copy assets when a starter is chosen)
- Test: `tests/DeskWall.Designer.Tests/PropertySchemaTests.cs` (`AllTypes` gains `DialDef`)

**Interfaces:**
- Consumes: `DialDef` from Task 2 (property names `Fraction, Track, Fill, Threshold, ThresholdFill, Thickness, StartAngle, Sweep`).

- [ ] **Step 1: Failing test.** In `PropertySchemaTests.AllTypes()` add
`yield return [new DialDef { Id = "d", Rect = R, Fraction = PropertyValue.Literal(0.5) }, "Fraction"];`
Run `dotnet test tests/DeskWall.Designer.Tests --filter FullyQualifiedName~PropertySchemaTests`. Expected: the two theories fail for `DialDef` with `NotSupportedException: no property schema for DialDef`.

- [ ] **Step 2: Schema + layer name.** In `PropertySchema.For` add `DialDef => DialProps,` and:

```csharp
private static readonly Prop[] DialProps =
[
    new("Fraction", Editor.Number, null, c => ((DialDef)c).Fraction, (c, v) => ((DialDef)c).Fraction = v),
    new("Track", Editor.Color, null, c => ((DialDef)c).Track, (c, v) => ((DialDef)c).Track = v),
    new("Fill", Editor.Color, null, c => ((DialDef)c).Fill, (c, v) => ((DialDef)c).Fill = v),
    new("Threshold", Editor.Number, null, c => ((DialDef)c).Threshold, (c, v) => ((DialDef)c).Threshold = v),
    new("ThresholdFill", Editor.Color, null, c => ((DialDef)c).ThresholdFill, (c, v) => ((DialDef)c).ThresholdFill = v),
    new("Thickness", Editor.Number, null, c => ((DialDef)c).Thickness, (c, v) => ((DialDef)c).Thickness = v),
    new("StartAngle", Editor.Number, null, c => ((DialDef)c).StartAngle, (c, v) => ((DialDef)c).StartAngle = v),
    new("Sweep", Editor.Number, null, c => ((DialDef)c).Sweep, (c, v) => ((DialDef)c).Sweep = v),
];
```

(Match the exact shape of `BarProps` in the file: if it is a `List<Prop>` or built differently, follow it.) In `LayersPanel.TypeName` add `DialDef => "dial",`. Run the designer tests: pass.

- [ ] **Step 3: Ship icons with the designer and copy them on first run.** In `DeskWall.Designer.csproj` add beside the starters item:

```xml
<None Include="..\..\assets\weather\*.png;..\..\assets\weather\LICENSE" Link="assets\weather\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
```

In `FirstRun.UseStarter_Click`, after `scaled.Save(dest);` add `CopyAssets();` and:

```csharp
/// <summary>Starters reference weather icons as runtime:assets/weather/<code>.png. Copy the set that
/// ships beside the exe into the runtime dir once; never overwrite a file that is already there.</summary>
private static void CopyAssets()
{
    var src = Path.Combine(AppContext.BaseDirectory, "assets", "weather");
    if (!Directory.Exists(src)) return;
    var dst = Paths.InRuntime("assets", "weather");
    Directory.CreateDirectory(dst);
    foreach (var f in Directory.EnumerateFiles(src))
    {
        var target = Path.Combine(dst, Path.GetFileName(f));
        if (!File.Exists(target)) File.Copy(f, target);
    }
}
```

Add a test if `FirstRun` logic is testable without a window (it is a `Window`; if not, put `CopyAssets` on `ShellState` as `public static void CopyAssets(string fromDir)` and test it under `DESKWALL_HOME`: creates files, does not overwrite). Prefer the `ShellState` placement.

- [ ] **Step 4: Build, both test suites, commit**

`dotnet build` 0 warnings; `dotnet test tests/DeskWall.Designer.Tests` and `tests/DeskWall.Core.Tests` pass. Commit: `Designer: dial in the properties and layers panels; weather icons shipped and copied on first run`.

---

### Task 6: Controller: merge, live registration, Gate 7 states, cost

- [ ] Merge `lane/w-hardware`, `lane/w-dial` (Tasks 2+3), `lane/w-recipes` into `v1` in that order; run `dotnet build` (0 warnings), both suites; re-run `StarterLayoutTests` now that `dial` exists.
- [ ] Dispatch Task 5 lane; merge; suites green.
- [ ] One Opus review of the whole diff `v1` since d7a21b6; one fix wave; re-review.
- [ ] `dotnet publish src/DeskWall.Daemon -c Release -r win-x64` (Installer dir on PATH per CLAUDE.md), copy to `%LOCALAPPDATA%\Programs\DeskWall\` after stopping the daemon; republish the designer there too (Task 5 changed it). Restart with `deskwall install`.
- [ ] Copy `assets\weather` to `%LOCALAPPDATA%\DeskWall\assets\weather`. Copy `layouts\column-system.json` to `%LOCALAPPDATA%\DeskWall\layouts\column-system.json` and point both display signatures in `layouts.json` at it (keep `clock-disks.json` as the fallback file).
- [ ] Wait two minutes; screenshot the column (`deskwall verify` saves one) and look at it: four arcs, numbers inside, temperature and icon, VPN line, drives.
- [ ] Gate 7 states, each via `deskwall --home <scratch> tick --layout <copy> --force --no-apply --no-shortcuts` on a copy of the layout, then open the produced `deskwall.jpg`: (a) `hardware` source removed (blank dials at fraction 0 plus default text); (b) weather URL broken (icon plate, no number); (c) Tailscale command path wrong (VPN line shows default empty). Note anything ugly and fix the layout, not the code.
- [ ] Cost: four-minute external sample of the resident daemon with the new layout (the PowerShell sampler used on 2026-09-21 in the spike results), plus the daemon log's tick lines. Record in `docs/architecture.md` under the budget section: idle CPU with the 10 s sampler, private bytes, handles, clock-only tick.
- [ ] `docs/architecture.md` process model: one paragraph on the in-source sampler timer.
- [ ] Commit docs; update `CLAUDE.md` file table if a new folder (`assets/`) needs a row.

---

## Self-review

- Spec coverage: section 3 → Task 1; section 4 → Task 2 (golden included); section 5 → Tasks 3, 4, 5 (prefix, icons, copy); section 6 → Task 4 (source + text); section 7 → Task 4 + Task 6 (registration); section 8 → each task's docs step plus Task 6 for architecture; section 9 → tests in Tasks 1 to 5 and the live/cost steps in Task 6; section 10 is out of scope by design.
- Type consistency: `HardwareSource(string, TimeSpan, TimeSpan, TimeSpan, IHardwareReader, bool autoStart)` used identically in tests and `FromDef`; `ResolvedDial` positional order `(Id, Rect, Z, Fraction, Track, Fill, Thickness, StartAngle, Sweep)` matches resolver, tests and renderer; `DialDef` property names match `DialProps` and the scaler; `DrawArc(Rect, float, float, float, Color)` matches the renderer call.
- Deviation flagged inside the plan: Task 4's dial captions versus the spec's Gate 3 table, to be ruled on at merge.
