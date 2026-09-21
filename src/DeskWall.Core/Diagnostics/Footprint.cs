using System.Runtime;
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

    /// <summary>What runs after a tick: give back what the tick used, then trim.
    /// <para>
    /// <see cref="Trim"/> alone pages memory out but leaves it committed, which is what the first
    /// native-AOT budget run measured (48-79 MB private bytes idle, 1.6 MB working set). An
    /// aggressive, compacting gen2 collection is the runtime's own "this process is going idle"
    /// gesture: it compacts the large object heap and decommits the freed regions instead of
    /// keeping them for the next allocation. Once a minute, on a process that is otherwise asleep,
    /// its cost is not the point; the committed footprint between wakes is.
    /// </para></summary>
    public static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Trim();
    }

    public string Short() => $"{WorkingSetBytes / 1048576.0:0.0} MB . cpu {TotalCpu.TotalSeconds:0.0} s . {Handles} h . {Threads} t";

    private static int s_threads = -1;

    private static int ThreadCount()
    {
        // Finding 5: Process.Threads is not cheap - it issues NtQuerySystemInformation
        // (SystemProcessInformation), a buffer sized for every process and thread on the machine,
        // typically a few hundred KB on the LOH. The tooltip reads this on every wake, outside
        // anything TickTimings measures. The daemon creates no threads of its own, so read it once.
        // Fall back to 0 rather than fail the tooltip.
        if (s_threads >= 0) return s_threads;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return s_threads = p.Threads.Count;
        }
        catch (Exception) { return s_threads = 0; }
    }
}
