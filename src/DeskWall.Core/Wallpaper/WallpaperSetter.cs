using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Display;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace DeskWall.Core.Wallpaper;

/// <summary>Per-monitor wallpaper through IDesktopWallpaper. Setting an unchanged path still reloads the image.</summary>
public static unsafe class WallpaperSetter
{
    private static IDesktopWallpaper* Open()
    {
        Com.EnsureInitialized();
        var clsid = typeof(DesktopWallpaper).GUID;
        var iid = typeof(IDesktopWallpaper).GUID;
        IDesktopWallpaper* dw;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, &iid, (void**)&dw).ThrowOnFailure();
        return dw;
    }

    public static void Set(string monitorId, string imagePath)
    {
        var dw = Open();
        try
        {
            dw->SetPosition(DESKTOP_WALLPAPER_POSITION.DWPOS_FILL);
            fixed (char* m = monitorId) fixed (char* p = imagePath) dw->SetWallpaper(m, p);
        }
        finally { dw->Release(); }
    }

    public static string? Get(string monitorId)
    {
        var dw = Open();
        try
        {
            PWSTR path;
            try { fixed (char* m = monitorId) dw->GetWallpaper(m, &path); }
            catch (COMException) { return null; }
            var s = path.ToString();
            PInvoke.CoTaskMemFree(path);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally { dw->Release(); }
    }

    private static string RestoreFile => Paths.InRuntime("restore.json");

    /// <summary>Record the pre-DeskWall wallpaper per monitor, once. Used by uninstall.</summary>
    public static void RecordRestorePoint()
    {
        if (File.Exists(RestoreFile)) return;
        var map = new Dictionary<string, string>();
        foreach (var m in Monitors.Enumerate())
            if (Get(m.WallpaperMonitorId) is { } p) map[m.WallpaperMonitorId] = p;
        File.WriteAllText(RestoreFile, JsonSerializer.Serialize(map, RestoreJsonContext.Default.DictionaryStringString));
    }

    public static void Restore()
    {
        if (!File.Exists(RestoreFile)) return;
        var map = JsonSerializer.Deserialize(File.ReadAllText(RestoreFile), RestoreJsonContext.Default.DictionaryStringString) ?? new();
        foreach (var (id, path) in map) if (File.Exists(path)) Set(id, path);
        File.Delete(RestoreFile);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class RestoreJsonContext : JsonSerializerContext;
