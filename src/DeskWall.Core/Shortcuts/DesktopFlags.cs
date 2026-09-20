using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Shortcuts;

/// <summary>Enforces the placement preconditions (auto-arrange and snap-to-grid off) and remembers
/// what to put back.</summary>
public static class DesktopFlags
{
    private const DesktopFolderFlags Mask = DesktopFolderFlags.AutoArrange | DesktopFolderFlags.SnapToGrid;

    private static string FilePath => Paths.InRuntime("desktop-flags.json");

    /// <summary>Turn off auto-arrange and snap-to-grid if on. Saves the original values the FIRST time
    /// only, so repeated ticks cannot overwrite the genuine original.</summary>
    public static void EnsurePlacementAllowed()
    {
        var current = DesktopView.Flags();
        if ((current & Mask) == 0) return;
        if (!File.Exists(FilePath))
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new SavedFlags { Original = (uint)(current & Mask) }, DesktopFlagsJsonContext.Default.SavedFlags));
        DesktopView.SetFlags(Mask, DesktopFolderFlags.None);
    }

    /// <summary>Restore the saved flags and delete the file. No-op when there is nothing saved.</summary>
    public static void Restore()
    {
        if (!File.Exists(FilePath)) return;
        SavedFlags? saved;
        try { saved = JsonSerializer.Deserialize(File.ReadAllText(FilePath), DesktopFlagsJsonContext.Default.SavedFlags); }
        catch (JsonException) { saved = null; }
        if (saved is not null) DesktopView.SetFlags(Mask, (DesktopFolderFlags)saved.Original & Mask);
        File.Delete(FilePath);
    }

    public sealed class SavedFlags
    {
        public uint Original { get; set; }
    }
}

[JsonSerializable(typeof(DesktopFlags.SavedFlags))]
internal sealed partial class DesktopFlagsJsonContext : JsonSerializerContext;
