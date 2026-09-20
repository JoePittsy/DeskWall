using DeskWall.Core.Resolve;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DeskWall.Core.Shortcuts;

public sealed record ShortcutSpec(string Target, string Arguments, string WorkingDirectory, string Description, string IconPath);

/// <summary>.lnk files through IShellLinkW + IPersistFile. Targets of the form "steam://..." (a URI) are
/// written as the target path verbatim: the shell launches URIs from .lnk targets via their protocol handler.
/// A target with arguments is split on the first unquoted space; a quoted program path is honoured.</summary>
public static unsafe class ShortcutFiles
{
    public static string DesktopDir() => Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    public static ShortcutSpec SpecFor(ResolvedShortcut s, string iconPath)
    {
        var t = s.Target.Trim();
        string prog, args;
        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && !uri.IsFile) { prog = t; args = ""; }
        else if (t.StartsWith('"')) { var end = t.IndexOf('"', 1); prog = end < 0 ? t.Trim('"') : t[1..end]; args = end < 0 ? "" : t[(end + 1)..].Trim(); }
        else { var sp = t.IndexOf(' '); prog = sp < 0 ? t : t[..sp]; args = sp < 0 ? "" : t[(sp + 1)..].Trim(); }
        var wd = Path.IsPathRooted(prog) && !prog.Contains("://") ? (Path.GetDirectoryName(prog) ?? "") : "";
        return new ShortcutSpec(prog, args, wd, s.Tooltip, iconPath);
    }

    public static void Write(string lnkPath, ShortcutSpec spec)
    {
        Com.EnsureInitialized();
        ShellLink.CreateInstance<IShellLinkW>(out var link).ThrowOnFailure();
        try
        {
            link->SetPath(spec.Target);
            link->SetArguments(spec.Arguments);
            link->SetWorkingDirectory(spec.WorkingDirectory);
            link->SetDescription(spec.Description);
            link->SetIconLocation(spec.IconPath, 0);
            link->SetShowCmd(SHOW_WINDOW_CMD.SW_SHOWNORMAL);

            link->QueryInterface<IPersistFile>(out var pf).ThrowOnFailure();
            try { pf->Save(lnkPath, true); }
            finally { pf->Release(); }
        }
        finally { link->Release(); }
    }

    public static ShortcutSpec? Read(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        Com.EnsureInitialized();
        ShellLink.CreateInstance<IShellLinkW>(out var link).ThrowOnFailure();
        try
        {
            IPersistFile* pf;
            link->QueryInterface(out pf).ThrowOnFailure();
            try
            {
                pf->Load(lnkPath, STGM.STGM_READ);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return null;
            }
            finally { pf->Release(); }

            Span<char> pathBuf = stackalloc char[1024];
            string target;
            fixed (char* p = pathBuf)
            {
                var pw = new PWSTR(p);
                link->GetPath(pw, pathBuf.Length, null, (uint)SLGP_FLAGS.SLGP_RAWPATH);
                target = pw.ToString();
            }

            Span<char> argsBuf = stackalloc char[260];
            link->GetArguments(argsBuf);
            var args = FromBuffer(argsBuf);

            Span<char> wdBuf = stackalloc char[260];
            link->GetWorkingDirectory(wdBuf);
            var wd = FromBuffer(wdBuf);

            Span<char> descBuf = stackalloc char[260];
            link->GetDescription(descBuf);
            var desc = FromBuffer(descBuf);

            Span<char> iconBuf = stackalloc char[260];
            link->GetIconLocation(iconBuf, out _);
            var icon = FromBuffer(iconBuf);

            return new ShortcutSpec(target, args, wd, desc, icon);
        }
        finally { link->Release(); }
    }

    public static void Delete(string lnkPath)
    {
        if (File.Exists(lnkPath)) File.Delete(lnkPath);
    }

    private static string FromBuffer(Span<char> buf)
    {
        var end = buf.IndexOf('\0');
        return new string(end < 0 ? buf : buf[..end]);
    }
}
