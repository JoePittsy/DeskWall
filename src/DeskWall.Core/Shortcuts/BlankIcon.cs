using DeskWall.Core.Render;

namespace DeskWall.Core.Shortcuts;

/// <summary>A fully transparent 256 px icon used as the shortcut icon for the desktop cover
/// shortcuts (the cover art is the composed wallpaper underneath; the icon itself must be
/// invisible). Written once into the runtime dir and reused after that.</summary>
public static class BlankIcon
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
        return path;
    }
}
