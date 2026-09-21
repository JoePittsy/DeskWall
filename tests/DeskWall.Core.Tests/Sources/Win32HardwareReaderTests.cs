using DeskWall.Core.Sources.Hardware;
using Xunit;
using Xunit.Abstractions;

/// <summary>Plausibility facts against the real machine. They assert only what must hold on any
/// Windows box, so they stay green on a machine with no NVIDIA GPU; the output line records what
/// this machine actually reported.</summary>
public class Win32HardwareReaderTests(ITestOutputHelper output)
{
    [Fact]
    public void Cpu_Times_Are_Readable_And_Monotonic()
    {
        var r = new Win32HardwareReader();
        var a = r.ReadCpu();
        Assert.NotNull(a);
        // GetSystemTimes moves on the 15.6 ms scheduler quantum, so spin long enough to cross a few.
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 100) { }
        var b = r.ReadCpu();
        Assert.NotNull(b);
        output.WriteLine($"cpu a={a}, b={b}, load={CpuTimes.Load(a!.Value, b!.Value)}");
        Assert.True(b!.Value.Kernel + b.Value.User >= a!.Value.Kernel + a.Value.User);
    }

    [Fact]
    public void Memory_Is_Readable_And_Plausible()
    {
        var m = new Win32HardwareReader().ReadMemory();
        Assert.NotNull(m);
        output.WriteLine($"memory used={m!.Value.UsedBytes} total={m.Value.TotalBytes}");
        Assert.True(m!.Value.UsedBytes > 0);
        Assert.True(m.Value.TotalBytes > m.Value.UsedBytes);
    }

    [Fact]
    public void Gpu_Is_Absent_Or_In_Range()
    {
        var r = new Win32HardwareReader();
        var g = r.ReadGpu();
        output.WriteLine($"HasGpu={r.HasGpu} ReadGpu={(g is null ? "null" : g.ToString())}");
        if (g is null) return;
        Assert.InRange(g.Value.Utilization, 0, 1);
        Assert.InRange(g.Value.MemoryFraction, 0, 1);
        Assert.InRange(g.Value.TemperatureC, 0, 150);
    }
}
