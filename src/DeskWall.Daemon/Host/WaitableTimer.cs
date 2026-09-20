using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DeskWall.Daemon.Host;

/// <summary>CreateWaitableTimerEx wrapper. Absolute due time, so a machine that suspends past the
/// due time fires the moment it resumes instead of restarting the delay.</summary>
internal sealed unsafe class WaitableTimer : IDisposable
{
    private const uint TimerAllAccess = 0x1F0003;   // SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_ALL_ACCESS

    public SafeFileHandle Handle { get; }

    public WaitableTimer()
    {
        Handle = PInvoke.CreateWaitableTimerEx(null, (string?)null, 0, TimerAllAccess);
        if (Handle.IsInvalid) throw new InvalidOperationException($"CreateWaitableTimerEx failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>The raw handle, for MsgWaitForMultipleObjectsEx. Keep the owner alive across the wait.</summary>
    public HANDLE Raw => (HANDLE)Handle.DangerousGetHandle();

    public void SetDue(DateTimeOffset dueUtc)
    {
        // Absolute FILETIME (UTC, 100 ns ticks since 1601): positive means absolute, negative relative.
        long ft = dueUtc.UtcDateTime.ToFileTimeUtc();
        if (!PInvoke.SetWaitableTimer(Handle, in ft, 0, null, null, false))
            throw new InvalidOperationException($"SetWaitableTimer failed: {Marshal.GetLastWin32Error()}");
    }

    public void Cancel() => PInvoke.CancelWaitableTimer(Handle);

    public void Dispose() => Handle.Dispose();
}
