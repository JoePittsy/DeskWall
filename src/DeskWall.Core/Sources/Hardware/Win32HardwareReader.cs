using System.Runtime.InteropServices.ComTypes;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace DeskWall.Core.Sources.Hardware;

/// <summary>GetSystemTimes for CPU, GlobalMemoryStatusEx for RAM, NVML for the GPU. Every call is
/// microseconds and allocation-free; none throws (a failed native call returns null).</summary>
public sealed unsafe class Win32HardwareReader : IHardwareReader, IDisposable
{
    private readonly NvmlGpuReader? _gpu = NvmlGpuReader.TryCreate();

    public bool HasGpu => _gpu is not null;

    /// <summary>Shuts NVML down and unloads it. GetSystemTimes and GlobalMemoryStatusEx hold
    /// nothing, so there is nothing else to let go of.</summary>
    public void Dispose() => _gpu?.Dispose();

    /// <summary>CsWin32 gives GetSystemTimes a pointer overload and a friendly `out FILETIME` one
    /// with [OverloadResolutionPriority(1)], so the out form is the only one that binds; FILETIME
    /// here is System.Runtime.InteropServices.ComTypes.FILETIME, whose halves are signed ints.</summary>
    public CpuTimes? ReadCpu()
    {
        if (!PInvoke.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        return new CpuTimes(ToUlong(idle), ToUlong(kernel), ToUlong(user));
    }

    private static ulong ToUlong(FILETIME t) => ((ulong)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

    public MemoryReading? ReadMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!PInvoke.GlobalMemoryStatusEx(&ms)) return null;
        return new MemoryReading(ms.ullTotalPhys - ms.ullAvailPhys, ms.ullTotalPhys);
    }

    public GpuReading? ReadGpu() => _gpu?.Read();
}
