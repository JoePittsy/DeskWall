using Windows.Win32;
using Windows.Win32.System.Com;

namespace DeskWall.Core;

/// <summary>Per-thread COM init, idempotent. Every lane that touches a COM interface calls this first.</summary>
public static unsafe class Com
{
    [ThreadStatic] private static bool _done;

    public static void EnsureInitialized()
    {
        if (_done) return;
        // S_FALSE (already initialised) and RPC_E_CHANGED_MODE 0x80010106 (thread is already MTA,
        // which .NET sets up on the main thread) both mean COM is usable. Neither is an error here.
        PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
        _done = true;
    }
}
