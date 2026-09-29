using Windows.Win32;

namespace DeskWall.Core;

/// <summary>Names of the kernel objects and windows that belong to one runtime directory, shared by
/// the daemon and the designer so both agree on which daemon (and which designer) is "this home's".
/// <para>What a single-instance lock protects is the runtime dir: two processes on one home race on
/// its files, two on different homes share none of them. The default home keeps the old names byte
/// for byte, so an installed daemon, an older build and a bare `deskwall run` are unaffected; any
/// other home (DESKWALL_HOME or `--home`) gets a hash suffix of its own, which is what lets a scratch
/// daemon or designer run beside the owner's live ones.</para></summary>
public static class RuntimeInstance
{
    /// <summary>The daemon's hidden host window class. The same for every daemon; the title is what
    /// tells them apart.</summary>
    public const string WindowClass = "DeskWallHost";

    /// <summary>"" for the default home, ".&lt;16 hex&gt;" for any other. A path cannot be a kernel
    /// object name (the backslash is the namespace separator), so it is hashed.</summary>
    public static string HomeSuffix(string home)
    {
        var full = Path.GetFullPath(home).TrimEnd('\\');
        var standard = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWall")).TrimEnd('\\');
        if (string.Equals(full, standard, StringComparison.OrdinalIgnoreCase)) return "";
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(full.ToUpperInvariant()));
        return "." + Convert.ToHexString(digest, 0, 8);
    }

    public static string DaemonLockName(string home) => @"Local\DeskWall.Daemon" + HomeSuffix(home);
    public static string DesignerLockName(string home) => @"Local\DeskWall.Designer" + HomeSuffix(home);

    /// <summary>The default home keeps the title it always had (the class name), so a new `stop` or
    /// designer still finds a daemon from an older build; any other home's title is its lock name.</summary>
    public static string DaemonWindowTitle(string home) =>
        HomeSuffix(home).Length == 0 ? WindowClass : DaemonLockName(home);

    /// <summary>The running daemon's host window for <see cref="Paths.RuntimeDir"/>, or 0. FindWindow
    /// by class alone would find whichever daemon came first, including the owner's live one when
    /// the caller is on a scratch home.</summary>
    public static nint FindDaemonWindow() => PInvoke.FindWindow(WindowClass, DaemonWindowTitle(Paths.RuntimeDir));

    /// <summary>The process id behind <see cref="FindDaemonWindow"/>, or null when no daemon runs for
    /// this home.</summary>
    public static unsafe int? FindDaemonProcessId()
    {
        var hwnd = FindDaemonWindow();
        if (hwnd == 0) return null;
        uint pid;
        return PInvoke.GetWindowThreadProcessId((Windows.Win32.Foundation.HWND)hwnd, &pid) == 0 ? null : (int)pid;
    }
}
