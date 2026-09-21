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
    private bool _disposed;

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
        if (_disposed) return null;
        Utilization u; Memory m; uint t;
        if (_getUtil(_device, &u) != 0) return null;
        if (_getMem(_device, &m) != 0) return null;
        if (_getTemp(_device, 0 /* NVML_TEMPERATURE_GPU */, &t) != 0) return null;
        var memFrac = m.Total == 0 ? 0 : (double)m.Used / m.Total;
        return new GpuReading(u.Gpu / 100.0, memFrac, t);
    }

    /// <summary>Idempotent, and it has to be: nvmlShutdown balances nvmlInit_v2 one for one, and
    /// freeing the module twice drops a reference this instance never took.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown(); } catch (Exception) { }
        NativeLibrary.Free(_lib);
    }
}
