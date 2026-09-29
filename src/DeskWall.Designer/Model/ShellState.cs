using System.IO;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>One entry of the display selector: the signature key the store and the model use, and
/// the short form a human can pick from.</summary>
public sealed record DisplayChoice(string Key, string Label);

/// <summary>What the window opens: the document, the canvas it is authored on, and the file it came
/// from (null only when nothing was registered and this is a brand new layout).
/// <paramref name="Migrated"/>: the file on disk is v1 and <paramref name="Layout"/> is its v2
/// migration, so the first Apply backs the file up first (<see cref="ShellState.BackupV1"/>).</summary>
public sealed record OpenTarget(LayoutFile Layout, DisplaySignature Signature, string? Path, bool Migrated = false);

/// <summary>The shell's decisions that are not WPF: where a layout for a display gets written, what
/// the display selector offers, what the toolbar says. Separated from MainWindow so it can be
/// tested without a message pump.</summary>
public static class ShellState
{
    /// <summary>What to open for the display in front of the owner, given what
    /// <see cref="LayoutStore.Resolve"/> answered for it.
    /// <para>
    /// The daemon paints whatever Resolve returns, so the designer must open the same thing or it is
    /// editing a layout nobody can see. Resolve also takes the <em>closest</em> match and scales it,
    /// which is what happens over RDP: this session's signature is 1692x1031 or thereabouts while the
    /// store's entries are keyed at 3440x1440. Opening the scaled copy would let the owner edit a
    /// layout that exists nowhere on disk, and Apply would then write those shrunken rects back over
    /// the authored file.
    /// </para>
    /// <para>
    /// So: open the file Resolve pointed at, unscaled, in the coordinates it was authored in, and
    /// edit on the canvas the matched store key names. A layout edited over Remote Desktop is then
    /// identical to one edited at the console, and Apply writes the one file both signatures already
    /// resolve to. Only an empty store - no entry for any display - gets a new layout, and that one
    /// belongs to the display actually in front of the owner.
    /// </para>
    /// <para>
    /// The authored file is read for an exact match too: since v2 the resolution's layout is the
    /// <em>expansion</em> (the store expands at load), and a document opened from it would have no
    /// copies left to edit. A v1 file is migrated in memory (<see cref="LayoutMigrator"/>) so the
    /// designer only ever edits v2; if the migration is not equivalent the file opens as it is,
    /// because showing the owner something other than his wallpaper is worse than a v1 document.
    /// </para></summary>
    /// <param name="load">reads the authored file; null when it cannot be read. A resolution's file
    /// was readable a moment ago, so this only fires on a genuine race, and then the resolution's
    /// copy is opened with no path rather than being allowed to overwrite the authored one.</param>
    /// <param name="find">the widget lookup a v1 migration runs against.</param>
    public static OpenTarget OpenFrom(LayoutResolution? resolution, DisplaySignature display,
        Func<string, LayoutFile?> load, string defaultBaseImage, Func<string, WidgetTemplate?> find)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (resolution is null) return new OpenTarget(new LayoutFile { Version = 2, BaseImage = defaultBaseImage }, display, null);
        if (load(resolution.SourcePath) is not { } authored)
            return new OpenTarget(resolution.Layout, resolution.Scaled ? display : resolution.SourceSignature, null);
        if (authored.Version < 2 && authored.Copies is null && LayoutMigrator.Migrate(authored, find) is { Equivalent: true } m)
            return new OpenTarget(m.V2, resolution.SourceSignature, resolution.SourcePath, Migrated: true);
        return new OpenTarget(authored, resolution.SourceSignature, resolution.SourcePath);
    }

    /// <summary>Where <see cref="BackupV1"/> puts the v1 file: <c>column-system.v1.json</c> beside
    /// <c>column-system.json</c>, the same name <c>deskwall migrate</c> uses, so one refuses to
    /// overwrite the other's backup.</summary>
    public static string BackupPath(string layoutPath) => Path.ChangeExtension(layoutPath, ".v1.json");

    /// <summary>Before the first Apply of a migrated document: copy the file, while it is still v1
    /// on disk, to <see cref="BackupPath"/>. Never overwrites a backup already there, and never
    /// backs up a file that is no longer v1 (the owner ran <c>deskwall migrate</c> meanwhile).
    /// Returns the backup path when it wrote one.</summary>
    public static string? BackupV1(string layoutPath)
    {
        var backup = BackupPath(layoutPath);
        if (File.Exists(backup) || !File.Exists(layoutPath)) return null;
        if (LayoutFile.Load(layoutPath).Version >= 2) return null;
        File.Copy(layoutPath, backup);
        return backup;
    }

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

    /// <summary>The window's default size. 1440x900 is the smallest rectangle that still fits the
    /// three columns (gallery, preview, knobs) with the preview wide enough to show a 3440-wide
    /// wallpaper at a readable scale.</summary>
    public const double DefaultWidth = 1440, DefaultHeight = 900;

    /// <summary>Where to open the window: the remembered rectangle when it still lands on a monitor
    /// that exists now, otherwise null, meaning "default size, let Windows centre it".
    /// <para>Apollo streaming and RDP both change the monitor set under a closed designer, and a
    /// window restored onto a monitor that is no longer there cannot be dragged back.</para></summary>
    public static (double Left, double Top, double Width, double Height)? Placement(
        double? left, double? top, double? width, double? height, IEnumerable<Rect> monitorBounds)
    {
        if (left is not { } l || top is not { } t || width is not { } w || height is not { } h) return null;
        return OnScreen(l, t, w, h, monitorBounds) ? (l, t, w, h) : null;
    }

    /// <summary>Starters reference weather icons as runtime:assets/weather/&lt;code&gt;.png. Copy the
    /// set that ships beside the exe into the runtime dir once; never overwrite a file that is
    /// already there. A missing source directory (e.g. a build that has not linked the assets) is a
    /// no-op, not an error.</summary>
    public static void CopyAssets(string fromDir)
    {
        if (!Directory.Exists(fromDir)) return;
        var dst = Paths.InRuntime("assets", "weather");
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(fromDir))
        {
            var target = Path.Combine(dst, Path.GetFileName(f));
            if (!File.Exists(target)) File.Copy(f, target);
        }
    }
}
