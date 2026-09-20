using DeskWall.Core.Render;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace DeskWall.Core.Shortcuts;

/// <summary>A fully transparent 256 px icon used as the shortcut icon for the desktop cover
/// shortcuts (the cover art is the composed wallpaper underneath; the icon itself must be
/// invisible). Written once into the runtime dir and reused after that.</summary>
public static unsafe class BlankIcon
{
    public static string Ensure()
    {
        var path = Paths.InRuntime("blank.ico");
        if (File.Exists(path)) return path;
        var pngPath = path + ".png.tmp";
        using (var s = Surface.Create(256, 256)) { s.Clear(Color.Transparent); s.SavePng(pngPath); }
        var png = File.ReadAllBytes(pngPath);
        File.Delete(pngPath);
        using var bw = new BinaryWriter(File.Create(path + ".tmp"));
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);
        bw.Write((byte)0); bw.Write((byte)0);          // 256 x 256
        bw.Write((byte)0); bw.Write((byte)0);          // palette, reserved
        bw.Write((ushort)1); bw.Write((ushort)32);     // planes, bpp
        bw.Write((uint)png.Length); bw.Write((uint)22);
        bw.Write(png);
        bw.Flush(); bw.Close();
        File.Move(path + ".tmp", path, overwrite: true);
        // Explorer keeps a per-path image cache in memory. Writing a new blank.ico over one it has
        // already drawn does NOT invalidate that entry, and a stale entry renders as an opaque black
        // 48x48 square sitting on top of every cover - measured on JOES-PC 2026-09-20, where the POC's
        // four slots and the calibrate probe all drew black until the shell was told to flush.
        // SHCNE_ASSOCCHANGED is the documented "drop your cached icons" notification.
        PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
        return path;
    }
}
