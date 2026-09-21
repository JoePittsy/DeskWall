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
