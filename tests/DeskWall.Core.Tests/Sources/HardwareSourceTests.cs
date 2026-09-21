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
}
