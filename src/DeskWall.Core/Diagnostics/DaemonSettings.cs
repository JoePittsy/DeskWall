using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWall.Core.Diagnostics;

/// <summary>The daemon's read-only view of the designer's settings.json. The designer owns the file
/// (DeskWall.Designer.Model.Settings writes it, atomically); the daemon reads exactly one value out
/// of it at start and never writes it, so the two processes cannot race over anything.
/// <para>Deliberately its own tiny model rather than a shared type: Core is AOT-analysed and the
/// designer's Settings class lives in a WPF assembly the daemon must not reference. Unknown members
/// (lastLayoutPath, the shell's panel state) are ignored, so the designer can grow the file without
/// touching this.</para></summary>
public static class DaemonSettings
{
    /// <summary>Whether the tray icon is wanted. True when settings.json is missing, unreadable or
    /// says nothing: a daemon with no tray icon and no way to ask for one is unreachable, so every
    /// failure here errs towards showing it.</summary>
    public static bool TrayIconEnabled()
    {
        var path = Paths.InRuntime("settings.json");
        try
        {
            if (!File.Exists(path)) return true;
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), DaemonSettingsJsonContext.Default.DaemonSettingsFile);
            return file?.TrayIcon ?? true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>The one property of settings.json the daemon cares about.</summary>
public sealed class DaemonSettingsFile
{
    public bool TrayIcon { get; set; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DaemonSettingsFile))]
internal partial class DaemonSettingsJsonContext : JsonSerializerContext;
