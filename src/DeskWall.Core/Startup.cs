using Microsoft.Win32;

namespace DeskWall.Core;

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry "DeskWall" -> "&lt;exe&gt;" run.</summary>
public static class Startup
{
    public const string ValueName = "DeskWall";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Install(string exePath, string? subKey = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(subKey ?? RunKey, writable: true);
        k.SetValue(ValueName, $"\"{exePath}\" run", RegistryValueKind.String);
    }

    public static void Uninstall(string? subKey = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey ?? RunKey, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static string? Installed(string? subKey = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey ?? RunKey);
        return k?.GetValue(ValueName) as string;
    }
}
