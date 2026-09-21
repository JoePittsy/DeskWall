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
    public async Task Cpu_Load_Needs_Two_Readings_Then_Averages()
    {
        var r = new FakeReader();
        // 100 ticks elapsed each step; idle 50 then 20 -> loads 0.5, 0.8
        r.Cpu.Enqueue(new CpuTimes(0, 0, 0));
        r.Cpu.Enqueue(new CpuTimes(50, 60, 40));
        r.Cpu.Enqueue(new CpuTimes(70, 120, 80));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce(); s.SampleOnce();
        var rec = await s.RefreshAsync(default);
        Assert.Equal(2, N(rec, "samples"));
        Assert.Equal(0.65, N(rec, "cpu"), 3);
        Assert.Equal(65, N(rec, "cpuPct"));
        Assert.Equal(0.8, N(rec, "cpuNow"), 3);
    }

    [Fact]
    public async Task Memory_Publishes_Fraction_And_GB()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(8UL << 30, 16UL << 30));
        var s = Make(r);
        s.SampleOnce();
        var rec = await s.RefreshAsync(default);
        Assert.Equal(0.5, N(rec, "ram"), 3);
        Assert.Equal(50, N(rec, "ramPct"));
        Assert.Equal(8.6, N(rec, "ramUsedGB"), 1);     // 8 GiB = 8.6 GB (decimal), one decimal
        Assert.Equal(17.2, N(rec, "ramTotalGB"), 1);
    }

    [Fact]
    public async Task Gpu_Publishes_Utilisation_Memory_And_Temperature()
    {
        var r = new FakeReader();
        r.Gpu.Enqueue(new GpuReading(0.10, 0.20, 50));
        r.Gpu.Enqueue(new GpuReading(0.30, 0.40, 54));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce();
        var rec = await s.RefreshAsync(default);
        Assert.Equal(0.2, N(rec, "gpu"), 3);
        Assert.Equal(20, N(rec, "gpuPct"));
        Assert.Equal(0.3, N(rec, "gpuNow"), 3);
        Assert.Equal(0.3, N(rec, "gpuMemory"), 3);
        Assert.Equal(52, N(rec, "gpuTempC"));
        Assert.Equal(0.52, N(rec, "gpuTempFraction"), 3);
    }

    [Fact]
    public async Task No_Gpu_Means_No_Gpu_Fields()
    {
        var r = new FakeReader { HasGpu = false };
        r.Mem.Enqueue(new MemoryReading(1, 2));
        var s = Make(r);
        s.SampleOnce();
        var rec = await s.RefreshAsync(default);
        Assert.False(rec.Fields.ContainsKey("gpu"));
        Assert.False(rec.Fields.ContainsKey("gpuTempC"));
        Assert.True(rec.Fields.ContainsKey("ram"));
    }

    [Fact]
    public async Task Zero_Samples_Publishes_Only_Samples_And_Window()
    {
        var s = Make(new FakeReader());
        var rec = await s.RefreshAsync(default);
        Assert.Equal(0, N(rec, "samples"));
        Assert.Equal(300, N(rec, "window"));
        Assert.Equal(2, rec.Fields.Count);
    }

    [Fact]
    public async Task Fractions_Are_Rounded_To_Three_Decimals()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(1, 3));
        var s = Make(r);
        s.SampleOnce();
        var rec = await s.RefreshAsync(default);
        Assert.Equal(0.333, N(rec, "ram"));
    }

    [Fact]
    public async Task Failed_Reading_Is_Skipped_Not_Counted()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(null);
        r.Mem.Enqueue(new MemoryReading(1, 4));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce();
        var rec = await s.RefreshAsync(default);
        Assert.Equal(0.25, N(rec, "ram"), 3);
    }

    [Fact]
    public async Task Samples_Reports_The_Largest_Window_Count()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(1, 4)); r.Mem.Enqueue(new MemoryReading(1, 4));
        var s = Make(r);
        s.SampleOnce(); s.SampleOnce();
        Assert.Equal(2, N(await s.RefreshAsync(default), "samples"));
    }

    private sealed class ThrowingReader : IHardwareReader
    {
        public int Calls;
        public bool HasGpu => false;
        public CpuTimes? ReadCpu() => null;
        public GpuReading? ReadGpu() => null;
        public MemoryReading? ReadMemory()
            => ++Calls == 1 ? throw new InvalidOperationException("driver reset") : new MemoryReading(1, 4);
    }

    private sealed class DisposableReader : IHardwareReader, IDisposable
    {
        public int Disposals;
        public bool HasGpu => false;
        public CpuTimes? ReadCpu() => null;
        public MemoryReading? ReadMemory() => null;
        public GpuReading? ReadGpu() => null;
        public void Dispose() => Disposals++;
    }

    /// <summary>The sampler runs on a System.Threading.Timer, and an exception out of a timer
    /// callback kills the process. A reader that throws must cost one sample, not the daemon.</summary>
    [Fact]
    public async Task Reader_That_Throws_Does_Not_Escape_And_The_Next_Sample_Still_Counts()
    {
        var r = new ThrowingReader();
        var s = Make2(r, TimeSpan.FromSeconds(10), autoStart: false);
        s.SampleOnce();                              // throws inside; must not propagate
        s.SampleOnce();
        var rec = await s.RefreshAsync(default);
        Assert.Equal(1, N(rec, "samples"));
        Assert.Equal(0.25, N(rec, "ram"), 3);
        Assert.Equal(1, s.ReaderFaults);
    }

    [Fact]
    public async Task Timer_Samples_On_Its_Own_Once_RefreshAsync_Has_Started_It()
    {
        var r = new FakeReader();
        for (var i = 0; i < 50; i++) r.Mem.Enqueue(new MemoryReading(1, 4));
        using var s = Make2(r, TimeSpan.FromMilliseconds(50), autoStart: true);
        await s.RefreshAsync(default);               // starts the timer
        await Task.Delay(300);
        var rec = await s.RefreshAsync(default);
        Assert.True(N(rec, "samples") >= 2, $"samples was {N(rec, "samples")}");
    }

    [Fact]
    public void Dispose_Disposes_A_Reader_That_Holds_Resources()
    {
        var r = new DisposableReader();
        var s = Make2(r, TimeSpan.FromSeconds(10), autoStart: false);
        s.Dispose();
        s.Dispose();                                 // idempotent: NVML must not be shut down twice
        Assert.Equal(1, r.Disposals);
        Make(new FakeReader()).Dispose();            // a reader that holds nothing is simply left alone
    }

    private static HardwareSource Make2(IHardwareReader r, TimeSpan sample, bool autoStart)
        => new("hw", TimeSpan.FromSeconds(60), sample, TimeSpan.FromSeconds(300), r, autoStart);

    private sealed class BlockingReader : IHardwareReader, IDisposable
    {
        public readonly ManualResetEventSlim EnteredReadMemory = new(false);
        public readonly ManualResetEventSlim ReleaseReadMemory = new(false);
        public int Disposals;
        public int ReadMemoryCalls;
        public bool HasGpu => false;
        public CpuTimes? ReadCpu() => null;
        public MemoryReading? ReadMemory()
        {
            ReadMemoryCalls++;
            EnteredReadMemory.Set();
            ReleaseReadMemory.Wait();
            return new MemoryReading(1, 4);
        }
        public GpuReading? ReadGpu() => null;
        public void Dispose() => Disposals++;
    }

    /// <summary>SampleOnce holds _lock for the whole reader call, including any blocking native
    /// call (NVML). Dispose must not tear down the reader while a sample is still inside it, or the
    /// library can be unloaded out from under a thread-pool thread mid-call.</summary>
    [Fact]
    public async Task Dispose_Waits_For_An_InFlight_Sample_Before_Disposing_The_Reader()
    {
        var r = new BlockingReader();
        var s = Make2(r, TimeSpan.FromSeconds(10), autoStart: false);
        var sampleTask = Task.Run(s.SampleOnce);
        Assert.True(r.EnteredReadMemory.Wait(TimeSpan.FromSeconds(5)), "sample never reached the reader");

        var disposeTask = Task.Run(s.Dispose);
        // The sample is still inside the reader; Dispose must block on the same lock, not proceed.
        var disposeFinishedEarly = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(300))) == disposeTask;
        Assert.False(disposeFinishedEarly, "Dispose returned while the sample was still in flight");

        r.ReleaseReadMemory.Set();
        var sampleDone = await Task.WhenAny(sampleTask, Task.Delay(TimeSpan.FromSeconds(5))) == sampleTask;
        Assert.True(sampleDone, "the in-flight sample never completed");
        var disposeDone = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5))) == disposeTask;
        Assert.True(disposeDone, "Dispose never returned after the sample was released");

        Assert.Equal(1, r.Disposals);

        s.SampleOnce();                               // after Dispose, must be a no-op
        Assert.Equal(1, r.ReadMemoryCalls);            // the reader was not touched again
        var rec = await s.RefreshAsync(default);
        Assert.Equal(1, N(rec, "samples"));            // only the original in-flight sample counted
    }

    private sealed class CountingReader : IHardwareReader
    {
        public int Calls;
        public bool HasGpu => false;
        public CpuTimes? ReadCpu() => null;
        public MemoryReading? ReadMemory() { Interlocked.Increment(ref Calls); return new MemoryReading(1, 4); }
        public GpuReading? ReadGpu() => null;
    }

    /// <summary>The old code started the timer with a check outside _lock, so a refresh racing a
    /// dispose could resurrect the sampler after Dispose had already let the reader go.</summary>
    [Fact]
    public async Task Dispose_Then_RefreshAsync_Never_Starts_The_Timer()
    {
        var r = new CountingReader();
        var s = Make2(r, TimeSpan.FromMilliseconds(20), autoStart: true);
        s.Dispose();
        await s.RefreshAsync(default);
        await Task.Delay(200);
        Assert.Equal(0, r.Calls);
    }

    /// <summary>Live finding, 2026-09-21 09:29:30: PeriodicSource anchors NextDue at lastRefresh + every,
    /// so a source first refreshed at :30 kept waking the daemon at :30 as well as the clock's :00 and
    /// the wallpaper was repainted twice a minute. The owner's ruling is "paint on the minute": the
    /// source is due on the next multiple of `every` from midnight, exactly as TimeSource is.</summary>
    [Fact]
    public void NextDue_Is_The_Next_Whole_Multiple_Of_Every_Not_LastRefresh_Plus_Every()
    {
        var s = Make(new FakeReader());   // every = 60 s
        var last = new DateTimeOffset(2026, 9, 21, 9, 28, 30, 258, TimeSpan.FromHours(1));
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 9, 29, 0, TimeSpan.FromHours(1)), s.NextDue(last, last));
        Assert.Equal(last, s.NextDue(null, last));   // never refreshed: due now

        var fiveMin = new HardwareSource("hw", TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(300), new FakeReader(), autoStart: false);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 9, 30, 0, TimeSpan.FromHours(1)), fiveMin.NextDue(last, last));
    }

    /// <summary>Adding a hardware source in the editor showed an empty value tree for up to 60 s,
    /// which reads as broken. The first refresh can only start the sampler, so it publishes nothing;
    /// the source says "due now" once so the very next wake paints real numbers.</summary>
    [Fact]
    public async Task A_First_Refresh_That_Published_Nothing_Is_Due_Again_Immediately()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(1, 4));
        var s = Make(r);
        var last = new DateTimeOffset(2026, 9, 21, 9, 28, 30, 258, TimeSpan.FromHours(1));

        var first = await s.RefreshAsync(default);
        Assert.Equal(0, N(first, "samples"));
        Assert.Equal(last, s.NextDue(last, last));

        s.SampleOnce();                                  // what the sampler does a moment later
        var second = await s.RefreshAsync(default);
        Assert.Equal(0.25, N(second, "ram"), 3);
    }

    /// <summary>The ruling NextDue exists for: once warm the source shares the clock's wake and the
    /// daemon paints once a minute. A reading of any kind ends the cold-start exception.</summary>
    [Fact]
    public async Task Once_Any_Ring_Has_Data_NextDue_Is_The_Whole_Minute_Again()
    {
        var r = new FakeReader();
        r.Mem.Enqueue(new MemoryReading(1, 4));
        var s = Make(r);
        s.SampleOnce();
        await s.RefreshAsync(default);
        var last = new DateTimeOffset(2026, 9, 21, 9, 28, 30, 258, TimeSpan.FromHours(1));
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 9, 29, 0, TimeSpan.FromHours(1)), s.NextDue(last, last));
    }

    /// <summary>Finding 1 again: a source that says "due now" forever pins the daemon's wake at
    /// MinDelay, four ticks a second. A reader that never produces a reading is a *successful*
    /// refresh publishing samples: 0, so the back-off never applies and only this bound protects the
    /// daemon. The cold-start exception is spent after one refresh, whatever the reader did.</summary>
    [Fact]
    public async Task A_Reader_That_Never_Reads_Does_Not_Pin_The_Daemon_Awake()
    {
        var s = Make(new FakeReader());                  // nothing queued: every reading is null
        var last = new DateTimeOffset(2026, 9, 21, 9, 28, 30, 258, TimeSpan.FromHours(1));
        var minute = new DateTimeOffset(2026, 9, 21, 9, 29, 0, TimeSpan.FromHours(1));

        await s.RefreshAsync(default);
        Assert.Equal(last, s.NextDue(last, last));       // one free wake
        await s.RefreshAsync(default);
        Assert.Equal(minute, s.NextDue(last, last));     // and no more
        await s.RefreshAsync(default);
        Assert.Equal(minute, s.NextDue(last, last));
    }

    /// <summary>A source the scheduler has never refreshed keeps its old answer: due now when
    /// lastRefresh is null, the whole minute otherwise. The cold-start exception is about a refresh
    /// that published nothing, not about the absence of one.</summary>
    [Fact]
    public void An_Unrefreshed_Source_Is_Unchanged()
    {
        var s = Make(new FakeReader());
        var last = new DateTimeOffset(2026, 9, 21, 9, 28, 30, 258, TimeSpan.FromHours(1));
        Assert.Equal(last, s.NextDue(null, last));
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 9, 29, 0, TimeSpan.FromHours(1)), s.NextDue(last, last));
    }

    /// <summary>The sampler's first reading is taken when it starts, not one `sample` later, so the
    /// second refresh has something to publish however long `sample` is.</summary>
    [Fact]
    public async Task The_Sampler_Takes_Its_First_Reading_Straight_Away()
    {
        var r = new FakeReader();
        for (var i = 0; i < 50; i++) r.Mem.Enqueue(new MemoryReading(1, 4));
        using var s = Make2(r, TimeSpan.FromSeconds(30), autoStart: true);   // far longer than the wait
        await s.RefreshAsync(default);                                       // starts the timer
        await Task.Delay(500);
        Assert.Equal(1, N(await s.RefreshAsync(default), "samples"));
    }
}
