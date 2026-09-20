using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core;

namespace DeskWall.Designer.Model;

/// <summary>runtime/settings.json, shared with the daemon (which reads TrayIcon at start).
/// Atomic write via a .tmp file, same discipline as LayoutFile.Save.</summary>
public sealed class Settings
{
    public bool TrayIcon { get; set; } = true;
    public string? LastLayoutPath { get; set; }
    public string? LastSignatureKey { get; set; }

    // ---- the shell's own layout (Task 8) ----------------------------------------------------
    // Which panels are showing, and where the window was, so reopening the designer puts the owner
    // back where he left off. Defaults: everything visible, and let Windows place the window.
    // The daemon's reader (DeskWall.Core.Diagnostics.DaemonSettings) ignores all of this.

    public bool ShowSources { get; set; } = true;
    public bool ShowProperties { get; set; } = true;
    public bool ShowLayers { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static string FilePath => Paths.InRuntime("settings.json");

    public static Settings Load()
    {
        if (!File.Exists(FilePath)) return new Settings();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.Settings) ?? new Settings();
        }
        catch (JsonException) { return new Settings(); }
    }

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Same tidy-runtime-dir discipline as LayoutFile.Save: never leave a .tmp behind.
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Settings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
