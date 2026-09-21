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
    private bool _disposed;
    private int _refreshes;

    /// <summary>Readings lost to a reader that threw. The readers are written not to throw, but this
    /// counts the times one did anyway; Core sources have no logger to report it to.</summary>
    public int ReaderFaults { get; private set; }

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

    /// <summary>Take one reading of every metric. Called by the timer; public so tests drive it.
    /// <para>Nothing escapes: this runs on a <see cref="Timer"/> callback, and an exception out of a
    /// timer callback takes the whole process down. The readers are written not to throw, but a
    /// driver reset under NVML, or a reader added later, must cost one sample and nothing more.</para></summary>
    public void SampleOnce()
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
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
            catch (Exception)
            {
                ReaderFaults++;
            }
        }
    }

    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            // Due time zero, not _sample: the first reading is taken as the sampler starts, so the
            // next refresh has numbers. It waited a whole `sample` before, and the editor showed an
            // empty value tree for that long. The callback takes _lock, so it queues behind this
            // refresh rather than racing it.
            if (_autoStart && _timer is null && !_disposed) _timer = new Timer(_ => SampleOnce(), null, TimeSpan.Zero, _sample);
            _refreshes++;
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

    /// <summary>Due on the next whole multiple of `every` from midnight, like <see cref="TimeSource"/>,
    /// not at lastRefresh + every. PeriodicSource's default anchored the schedule at whatever second
    /// the first refresh happened to run (09:28:30 on the day this shipped), so the daemon woke and
    /// repainted at :30 as well as the clock's :00 - two repaints a minute, against the owner's
    /// "sample every 10 s, paint on the minute" ruling. With `every` 60 s this is the clock's own
    /// boundary and the two sources share one wake.</summary>
    /// <param name="lastRefresh">null until the source has been refreshed once.</param>
    public override DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        // Cold start. The first refresh can only start the sampler, so it publishes nothing and
        // every bound property falls back to its default - which read as broken for up to a minute
        // when a hardware source was added in the editor. One free wake fixes that.
        // Bounded to exactly one, and spent whether or not the reader produced anything: a reader
        // that never reads is a *successful* refresh publishing samples: 0, so the scheduler's
        // back-off never applies to it, and an unconditional "due now" would pin the daemon's wake
        // at MinDelay - four ticks a second - which is finding 1 all over again. Once warm the
        // source is back on the whole minute and shares the clock's single wake.
        if (_refreshes == 1 && !HasAnyReading()) return now;
        var l = lastRefresh.Value;
        var dayStart = new DateTimeOffset(l.Year, l.Month, l.Day, 0, 0, 0, l.Offset);
        var sinceMidnight = (l - dayStart).Ticks;
        var floored = sinceMidnight - sinceMidnight % Every.Ticks;
        return dayStart.AddTicks(floored) + Every;
    }

    /// <summary>Read without _lock on purpose. SampleOnce holds that lock across a blocking native
    /// call (NVML), and this is called from the scheduler thread deciding the next wake; a stale
    /// read of three ints costs at most one extra wake, which is the thing being asked for anyway.</summary>
    private bool HasAnyReading() => _cpu.Count > 0 || _ram.Count > 0 || _gpu.Count > 0;

    /// <summary>Stops the sampler and lets go of whatever the reader holds (NVML, on this machine).
    /// Idempotent: the host may dispose a source it has already replaced, and shutting NVML down
    /// twice or freeing its module twice is not safe.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _timer?.Dispose();
        _timer = null;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            (_reader as IDisposable)?.Dispose();
        }
    }
}
