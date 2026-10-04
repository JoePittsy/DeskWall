using System.Text.Json;
using DeskWall.Core.Display;

namespace DeskWall.Core.Layout;

/// <summary>A layout that can be switched to: its file name (what the owner types and picks) and
/// where it is.</summary>
public sealed record LayoutChoice(string Name, string Path);

/// <summary>What <see cref="LayoutLibrary.Use"/> did. <paramref name="From"/> is the file the
/// display drew from before (null for an empty store); <paramref name="Keys"/> the display
/// signatures now naming <paramref name="To"/>; <paramref name="Imported"/> that the file was
/// copied into the library first; <paramref name="Changed"/> false when it was already in use and
/// nothing was written.</summary>
public sealed record LayoutSwitch(string? From, string To, IReadOnlyList<string> Keys, bool Imported, bool Changed);

/// <summary>A switch that cannot happen, as the one sentence the CLI prints and the designer
/// shows. Nothing has been written when it is thrown.</summary>
public sealed class LayoutSwitchException(string message) : Exception(message);

/// <summary>The layouts the owner can switch between, and the one switch the CLI (`deskwall layouts
/// use`, `deskwall theme`, #31) and the designer's layout picker (#73) share.
/// <para>The library is the runtime dir's <c>layouts\</c> folder, where the designer saves, plus any
/// file layouts.json names from elsewhere, so the one in use is always listed. Migrate backups
/// (<c>*.v1.json</c>) are not layouts anyone means to switch to. A file from outside the library
/// is copied in before it is used, so the repo's <c>layouts\</c> stay templates and the designer
/// never edits them by accident; a different file already holding that name is never
/// overwritten.</para>
/// <para>The daemon needs nothing from here: the switch is one write of layouts.json, which its
/// layout watcher already reacts to.</para></summary>
public static class LayoutLibrary
{
    /// <summary><c>&lt;runtime&gt;\layouts</c>.</summary>
    public static string DefaultDir => Paths.InRuntime("layouts");

    public static IReadOnlyList<LayoutChoice> List(LayoutStore store, string libraryDir)
    {
        IEnumerable<string> files = Directory.Exists(libraryDir) ? Directory.EnumerateFiles(libraryDir, "*.json") : [];
        return files
            .Where(f => !f.EndsWith(".v1.json", StringComparison.OrdinalIgnoreCase))
            .Concat(store.Entries.Values)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new LayoutChoice(Path.GetFileName(p), p))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Switch <paramref name="display"/> (and every display sharing its layout) to
    /// <paramref name="nameOrPath"/>: a library name, with or without <c>.json</c>, or a path to a
    /// layout file. Throws <see cref="LayoutSwitchException"/>, having written nothing, when the
    /// name is unknown or ambiguous, the file is missing, it is not a layout this build can paint,
    /// or its name is taken in the library by a different file.</summary>
    public static LayoutSwitch Use(LayoutStore store, DisplaySignature display, string nameOrPath, string libraryDir)
    {
        // A store can be long-lived (the designer holds one for its whole life): what `deskwall theme`
        // or `layouts set` wrote meanwhile must not be written back over.
        store.Reload();
        var choices = List(store, libraryDir);
        var path = Locate(choices, nameOrPath, libraryDir);
        Check(path);
        var imported = false;
        if (!choices.Any(c => LayoutStore.SameFile(c.Path, path)))
            (path, imported) = Import(path, libraryDir);
        var (from, keys) = store.Repoint(display, path);
        return new LayoutSwitch(from, path, keys, imported, Changed: from is null || !LayoutStore.SameFile(from, path));
    }

    /// <summary>A bare name is looked up in the library; anything with a folder in it, or a bare
    /// name the library does not have but the current folder does, is a path.</summary>
    private static string Locate(IReadOnlyList<LayoutChoice> choices, string nameOrPath, string libraryDir)
    {
        if (string.IsNullOrEmpty(Path.GetDirectoryName(nameOrPath)))
        {
            var named = choices.Where(c => Answers(c, nameOrPath)).ToList();
            if (named.Count == 1) return named[0].Path;
            if (named.Count > 1)
                throw new LayoutSwitchException($"'{nameOrPath}' could be {string.Join(" or ", named.Select(c => c.Path))}; give the path");
            if (!File.Exists(nameOrPath))
                throw new LayoutSwitchException(choices.Count == 0
                    ? $"no layout named '{nameOrPath}': {libraryDir} has none"
                    : $"no layout named '{nameOrPath}'; there is {string.Join(", ", choices.Select(c => Path.GetFileNameWithoutExtension(c.Name)))}");
        }
        var full = Path.GetFullPath(nameOrPath);
        if (!File.Exists(full)) throw new LayoutSwitchException($"no layout at {full}");
        return full;
    }

    private static bool Answers(LayoutChoice choice, string name)
        => string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileNameWithoutExtension(choice.Name), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refuse what the daemon would refuse at its next resolve (LayoutStore.TryLoad), now,
    /// with the reason, rather than switch to a desktop that stops repainting.</summary>
    private static void Check(string path)
    {
        LayoutFile file;
        try { file = LayoutFile.Load(path); }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new LayoutSwitchException($"{Path.GetFileName(path)} is not a layout DeskWall can read: {ex.Message}");
        }
        if (file.Version > LayoutStore.MaxVersion)
            throw new LayoutSwitchException($"{Path.GetFileName(path)} is layout version {file.Version}; this build reads up to {LayoutStore.MaxVersion}");
    }

    /// <summary>Copy a file from outside into the library under its own name. The same bytes already
    /// there is the same layout and is reused; different bytes are the owner's (perhaps edited in
    /// the designer) and are never overwritten.</summary>
    private static (string Path, bool Imported) Import(string source, string libraryDir)
    {
        var target = Path.Combine(Path.GetFullPath(libraryDir), Path.GetFileName(source));
        if (File.Exists(target))
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source))) return (target, false);
            throw new LayoutSwitchException(
                $"{target} already exists and is different from {source}; rename one of them, or delete the library copy if it is no longer wanted");
        }
        Directory.CreateDirectory(libraryDir);
        File.Copy(source, target);
        return (target, true);
    }
}
