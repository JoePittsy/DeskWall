using Windows.Win32;
using Windows.Win32.System.ProcessStatus;

namespace DeskWall.Core.Diagnostics;

/// <summary>What Task Manager would show for this process. Read without opening a process handle
/// (the pseudo-handle from GetCurrentProcess needs no close).</summary>
public sealed unsafe record Footprint(long PrivateBytes, long WorkingSetBytes, TimeSpan TotalCpu, int Handles, int Threads)
{
    public static Footprint Current()
    {
        var h = PInvoke.GetCurrentProcess();
        var pmc = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX) };
        PInvoke.GetProcessMemoryInfo(h, (PROCESS_MEMORY_COUNTERS*)&pmc, pmc.cb);
        uint handles = 0;
        PInvoke.GetProcessHandleCount(h, &handles);
        var threads = ThreadCount();
        return new Footprint((long)pmc.PrivateUsage, (long)pmc.WorkingSetSize, Environment.CpuUsage.TotalTime, (int)handles, threads);
    }

    /// <summary>Give freed pages back so Task Manager shows the idle number, not the render peak.</summary>
    public static void Trim() => PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);

    public string Short() => $"{WorkingSetBytes / 1048576.0:0.0} MB . cpu {TotalCpu.TotalSeconds:0.0} s . {Handles} h . {Threads} t";

    private static int ThreadCount()
    {
        // System.Diagnostics.Process would open a handle; the thread count is cheap to read from the
        // process snapshot API instead. Fall back to 0 rather than fail the tooltip.
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.Threads.Count;
        }
        catch (Exception) { return 0; }
    }
}
