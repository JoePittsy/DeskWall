using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskWall.Core.Scheduling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DeskWall.Daemon.Host;

/// <summary>Hidden top-level window plus message pump. Everything the OS tells us arrives here and
/// is turned into a WakeReason. Must be created and pumped on one thread (the main thread).</summary>
internal sealed unsafe class HostWindow : IDisposable
{
    public const uint WM_APP_WAKE = 0x8000 + 1;   // WPARAM = (int)WakeKind, posted by other threads
    public const uint WM_APP_TRAY = 0x8000 + 2;   // Shell_NotifyIcon callback message

    private const string ClassName = "DeskWallHost";

    private static HostWindow? s_instance;        // one per process; the WndProc is a static function pointer
    private readonly List<WakeReason> _pending = new();
    private ushort _atom;

    public HWND Handle { get; private set; }

    /// <summary>Raised on the pump thread, once per reason, after the pump has drained.</summary>
    public event Action<WakeReason>? Wake;

    /// <summary>(msg, wParam, lParam) of a WM_APP_TRAY notification, for TrayIcon.</summary>
    public event Action<uint, nuint, nint>? TrayMessage;

    public HostWindow()
    {
        if (s_instance is not null) throw new InvalidOperationException("one HostWindow per process");
        s_instance = this;
        global::DeskWall.Core.Com.EnsureInitialized();
        var hinst = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hinst,
                lpszClassName = cls,
            };
            _atom = PInvoke.RegisterClassEx(&wc);
            if (_atom == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            // WS_EX_TOOLWINDOW keeps it off the taskbar and out of Alt+Tab; it is never shown either way.
            Handle = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, cls, cls, WINDOW_STYLE.WS_OVERLAPPED,
                0, 0, 0, 0, HWND.Null, HMENU.Null, hinst, null);
            if (Handle.IsNull) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
        PInvoke.WTSRegisterSessionNotification(Handle, PInvoke.NOTIFY_FOR_THIS_SESSION);
    }

    /// <summary>Thread-safe: PostMessage(WM_APP_WAKE, kind).</summary>
    public void Post(WakeKind kind) => PInvoke.PostMessage(Handle, WM_APP_WAKE, (nuint)(int)kind, 0);

    /// <summary>Block until the timer fires or a message arrives, pump everything pending, and return
    /// the reasons raised (possibly empty when only unrelated messages came).</summary>
    public IReadOnlyList<WakeReason> WaitAndPump(WaitableTimer timer)
    {
        _pending.Clear();
        ReadOnlySpan<HANDLE> handles = [timer.Raw];
        // Blocks until the timer is signalled or any message is queued. No polling.
        var wait = PInvoke.MsgWaitForMultipleObjectsEx(handles, PInvoke.INFINITE, QUEUE_STATUS_FLAGS.QS_ALLINPUT,
            MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
        GC.KeepAlive(timer);
        // WAIT_OBJECT_0 is handle 0 (the timer); WAIT_OBJECT_0 + 1 means messages. The return value is
        // the only reliable answer: CreateWaitableTimerEx with no flags makes a synchronization timer,
        // so this wait has already reset it and a second probe would always say "not signalled".
        if (wait == WAIT_EVENT.WAIT_OBJECT_0) _pending.Add(new WakeReason(WakeKind.Timer));
        while (PInvoke.PeekMessage(out var msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            if (msg.message == PInvoke.WM_QUIT) { _pending.Add(new WakeReason(WakeKind.Shutdown, "quit")); break; }
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
        var result = _pending.ToArray();
        foreach (var r in result) Wake?.Invoke(r);
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        var self = s_instance;
        if (self is not null)
        {
            switch (msg)
            {
                case PInvoke.WM_DISPLAYCHANGE:
                    self._pending.Add(new WakeReason(WakeKind.DisplayChange,
                        $"{(int)(lParam.Value & 0xFFFF)}x{(int)((lParam.Value >> 16) & 0xFFFF)}"));
                    return (LRESULT)0;
                case PInvoke.WM_SETTINGCHANGE:
                    if ((uint)wParam.Value == (uint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETDESKWALLPAPER
                        || (lParam.Value != 0 && new PCWSTR((char*)lParam.Value).ToString() == "ImmersiveColorSet"))
                        self._pending.Add(new WakeReason(WakeKind.DisplayChange, "settingchange"));
                    return (LRESULT)0;
                case PInvoke.WM_WTSSESSION_CHANGE:
                    if ((uint)wParam.Value is PInvoke.WTS_SESSION_UNLOCK or PInvoke.WTS_REMOTE_CONNECT or PInvoke.WTS_CONSOLE_CONNECT)
                        self._pending.Add(new WakeReason(WakeKind.SessionUnlock, ((uint)wParam.Value).ToString()));
                    return (LRESULT)0;
                case PInvoke.WM_POWERBROADCAST:
                    if ((uint)wParam.Value == PInvoke.PBT_APMRESUMEAUTOMATIC)
                        self._pending.Add(new WakeReason(WakeKind.Timer, "resume"));
                    return (LRESULT)1;
                case WM_APP_WAKE:
                    self._pending.Add(new WakeReason((WakeKind)(int)wParam.Value, "posted"));
                    return (LRESULT)0;
                case WM_APP_TRAY:
                    self.TrayMessage?.Invoke(msg, wParam.Value, lParam.Value);
                    return (LRESULT)0;
                case PInvoke.WM_CLOSE:
                case PInvoke.WM_QUERYENDSESSION:
                case PInvoke.WM_ENDSESSION:
                    self._pending.Add(new WakeReason(WakeKind.Shutdown, msg.ToString()));
                    return msg == PInvoke.WM_QUERYENDSESSION ? (LRESULT)1 : (LRESULT)0;
            }
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (!Handle.IsNull)
        {
            PInvoke.WTSUnRegisterSessionNotification(Handle);
            PInvoke.DestroyWindow(Handle);
            Handle = HWND.Null;
        }
        if (_atom != 0)
        {
            fixed (char* cls = ClassName) PInvoke.UnregisterClass(cls, (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null));
            _atom = 0;
        }
        s_instance = null;
    }
}
