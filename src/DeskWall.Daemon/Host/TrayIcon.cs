using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DeskWall.Daemon.Host;

internal enum TrayCommand { OpenDesigner = 1, RefreshNow = 2, TogglePause = 3, Exit = 4 }

/// <summary>Shell_NotifyIcon wrapper. Menu: Open designer, Refresh now, Pause/Resume, Exit.
/// Left click is Open designer. The tooltip text is supplied by the caller; this only transports it.</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint Id = 1;
    private const int TipMax = 127;   // NOTIFYICONDATAW.szTip is 128 chars including the terminator

    private readonly HostWindow _host;
    private readonly HICON _icon;
    private bool _added;

    /// <summary>Raised on the pump thread.</summary>
    public event Action<TrayCommand>? Command;

    /// <summary>Flips the menu item between Pause and Resume.</summary>
    public bool Paused { get; set; }

    public TrayIcon(HostWindow host)
    {
        _host = host;
        _icon = LoadAppIcon();
        var nid = Data();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
                   | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        nid.uCallbackMessage = HostWindow.WM_APP_TRAY;
        nid.hIcon = _icon;
        SetTip(ref nid, "DeskWall");
        _added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &nid);
        if (_added)
        {
            // Version 4 puts the event in LOWORD(lParam) and the cursor position in wParam.
            nid.uVersion = PInvoke.NOTIFYICON_VERSION_4;
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &nid);
        }
        host.TrayMessage += OnTrayMessage;
    }

    public bool Added => _added;

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var nid = Data();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        SetTip(ref nid, text);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &nid);
    }

    private NOTIFYICONDATAW Data() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _host.Handle, uID = Id };

    private static void SetTip(ref NOTIFYICONDATAW nid, string text)
    {
        if (text.Length > TipMax) text = text[..TipMax];
        var span = nid.szTip.AsSpan();
        span.Clear();
        text.AsSpan().CopyTo(span);
    }

    private void OnTrayMessage(uint msg, nuint wParam, nint lParam)
    {
        var evt = (uint)(lParam & 0xFFFF);   // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event
        switch (evt)
        {
            case PInvoke.WM_CONTEXTMENU:
            case PInvoke.WM_RBUTTONUP:
                ShowMenu();
                break;
            case PInvoke.WM_LBUTTONUP:
            case PInvoke.NIN_SELECT:
                Command?.Invoke(TrayCommand.OpenDesigner);
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull) return;
        try
        {
            Append(menu, TrayCommand.OpenDesigner, "Open designer");
            Append(menu, TrayCommand.RefreshNow, "Refresh now");
            Append(menu, TrayCommand.TogglePause, Paused ? "Resume" : "Pause");
            Append(menu, TrayCommand.Exit, "Exit");
            PInvoke.GetCursorPos(out var pt);
            PInvoke.SetForegroundWindow(_host.Handle);   // or the menu never dismisses on an outside click
            var flags = (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON
                             | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN);
            // With TPM_RETURNCMD the raw return is the chosen command id, which CsWin32 types as BOOL.
            var chosen = PInvoke.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, _host.Handle, null);
            PInvoke.PostMessage(_host.Handle, 0, 0, 0);  // WM_NULL: the documented follow-up to TrackPopupMenu
            if (chosen.Value != 0) Command?.Invoke((TrayCommand)chosen.Value);
        }
        finally { PInvoke.DestroyMenu(menu); }
    }

    private static void Append(HMENU menu, TrayCommand id, string text)
    {
        fixed (char* p = text) PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, (nuint)(int)id, p);
    }

    private static HICON LoadAppIcon()
    {
        // The exe's own icon (ApplicationIcon in the csproj) lands at resource id 32512.
        var hinst = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null);
        var h = PInvoke.LoadIcon(hinst, (PCWSTR)(char*)32512);
        return h.IsNull ? PInvoke.LoadIcon(HINSTANCE.Null, (PCWSTR)(char*)32512) : h;   // fall back to the stock icon
    }

    public void Dispose()
    {
        _host.TrayMessage -= OnTrayMessage;
        if (_added)
        {
            var nid = Data();
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &nid);
            _added = false;
        }
        // _icon comes from LoadIcon on a resource: shared, and must not be destroyed.
    }
}
