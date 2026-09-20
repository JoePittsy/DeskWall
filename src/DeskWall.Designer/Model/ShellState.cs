using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;

namespace DeskWall.Designer.Model;

/// <summary>One entry of the display selector: the signature key the store and the model use, and
/// the short form a human can pick from.</summary>
public sealed record DisplayChoice(string Key, string Label);

/// <summary>The shell's decisions that are not WPF: where a layout for a display gets written, what
/// the display selector offers, what the toolbar says. Separated from MainWindow so it can be
/// tested without a message pump.</summary>
public static class ShellState
{
    /// <summary>Where a layout saved "for this display" goes: runtime/layouts/&lt;safe key&gt;.json.
    /// A display signature contains a device path, which is full of characters a file name cannot
    /// hold.</summary>
    public static string LayoutPathFor(DisplaySignature signature)
        => Paths.InRuntime("layouts", SafeFileName(signature.Key) + ".json");

    public static string SafeFileName(string signatureKey)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(signatureKey.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>What the display selector lists: every monitor attached right now, then every
    /// signature the store already has a layout for. Order matters - the display in front of the
    /// owner comes first - and a key that is both is listed once.</summary>
    public static IReadOnlyList<DisplayChoice> DisplayChoices(IEnumerable<string> monitorKeys, IEnumerable<string> storeKeys)
    {
        var list = new List<DisplayChoice>();
        foreach (var key in monitorKeys.Concat(storeKeys))
            if (!list.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
                list.Add(new DisplayChoice(key, DisplayLabel(key)));
        return list;
    }

    /// <summary>A signature key is a device path plus geometry, and the device path is 60 characters
    /// of GUID. The resolution and scale are what the owner is choosing between; the hardware id is
    /// only there to tell two identical panels apart, so it is cut to the model fragment the shell
    /// enumeration puts second (DISPLAY#DELA1C9#...).</summary>
    public static string DisplayLabel(string signatureKey)
    {
        DisplaySignature sig;
        try { sig = DisplaySignature.Parse(signatureKey); }
        catch (FormatException) { return signatureKey; }
        catch (IndexOutOfRangeException) { return signatureKey; }
        var parts = sig.DevicePath.Split('#');
        var device = parts.Length >= 2 && parts[1].Length > 0 ? parts[1] : sig.DevicePath;
        return $"{sig.Width}x{sig.Height} @ {sig.ScalePercent}%   {device}";
    }

    /// <summary>The toolbar's file name and dirty marker. A layout that has never been applied for
    /// this display has no path yet, and saying so is the only warning that Apply will create one.</summary>
    public static string FileLabel(string? path, bool dirty)
    {
        var name = path is null ? "(not saved for this display)" : Path.GetFileName(path);
        return dirty ? name + " *" : name;
    }

    /// <summary>Spec 5's banner: this layout was authored for another display and is being shown
    /// stretched to fit this one.</summary>
    public static string BannerText(string sourceSignatureKey) => $"scaled from {sourceSignatureKey}";

    /// <summary>Whether a remembered window rectangle is still usable. Icon positions and window
    /// positions both survive a resolution change badly (Apollo streaming changes it), so a restored
    /// window that would land off every monitor is thrown away rather than opened where nobody can
    /// reach it. Overlap is enough: a window half off the edge is still draggable.</summary>
    public static bool OnScreen(double left, double top, double width, double height, IEnumerable<Rect> monitorBounds)
    {
        if (width <= 0 || height <= 0) return false;
        foreach (var b in monitorBounds)
            if (left < b.Right && left + width > b.X && top < b.Bottom && top + height > b.Y) return true;
        return false;
    }
}
