using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Display;

namespace DeskWall.Core.Layout;

public sealed record LayoutResolution(LayoutFile Layout, string SourcePath, DisplaySignature SourceSignature, bool Scaled);

/// <summary>layouts.json in the runtime dir: { "layouts": { "&lt;signature key&gt;": "&lt;layout path&gt;" } }.
/// Layout paths are absolute or relative to the runtime dir.</summary>
public sealed class LayoutStore
{
    /// <summary>The only layout schema this build understands. Spec 5's migration chain starts here:
    /// a file from a future version is refused rather than half-read (finding 16).</summary>
    public const int MaxVersion = 1;

    private readonly string _storePath;
    private readonly string _baseDir;
    private readonly Action<string>? _onError;
    private Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="onError">Where a rejected or unreadable layout file is reported. The daemon
    /// passes RollingLog.Error; a caller with no log passes null and gets silence plus a null
    /// resolution.</param>
    public LayoutStore(string storePath, Action<string>? onError = null)
    {
        _storePath = Path.GetFullPath(storePath);
        _baseDir = Path.GetDirectoryName(_storePath)!;
        _onError = onError;
        Load();
    }

    public static LayoutStore Default(Action<string>? onError = null) => new(Paths.InRuntime("layouts.json"), onError);

    public IReadOnlyDictionary<string, string> Entries => _entries.ToDictionary(kv => kv.Key, kv => Resolve(kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file the daemon must watch: the store itself and all layout files.</summary>
    public IReadOnlyList<string> WatchPaths => [_storePath, .. _entries.Values.Select(Resolve)];

    /// <summary>Re-read layouts.json from disk. The daemon holds one store for its whole life, so an
    /// entry added by `deskwall layouts set` (a second process) is only visible after this.</summary>
    public void Reload() => Load();

    public void Set(DisplaySignature sig, string layoutPath) { _entries[sig.Key] = Path.GetFullPath(layoutPath); Save(); }

    public void Remove(DisplaySignature sig) { if (_entries.Remove(sig.Key)) Save(); }

    /// <summary>Exact match, else the closest by Similarity (ties: most recently written file), scaled
    /// to sig. Null when the store is empty or nothing in it can be read.
    /// <para>
    /// A layout registered for <em>this</em> signature that is missing, unreadable, or written by a
    /// future build is an error, not an invitation to substitute another display's layout: it is
    /// reported through onError and Resolve returns null, so the daemon keeps the wallpaper it already
    /// applied (spec 3.2) and the tray tooltip says to look in the log. Closest-match only applies
    /// when this signature has no entry at all (finding 4).
    /// </para></summary>
    public LayoutResolution? Resolve(DisplaySignature sig)
    {
        // One parse and, more importantly, one complaint per file per call: the exact-match branch and
        // the closest-match loop both look at the same entry.
        var loaded = new Dictionary<string, LayoutFile?>(StringComparer.OrdinalIgnoreCase);
        LayoutFile? Load(string p)
        {
            if (loaded.TryGetValue(p, out var cached)) return cached;
            var file = TryLoad(p);
            loaded[p] = file;
            return file;
        }

        if (_entries.TryGetValue(sig.Key, out var exact))
        {
            var path = Resolve(exact);
            if (!File.Exists(path))
            {
                _onError?.Invoke($"layout {path} registered for {sig.Key} is missing");
                return null;
            }
            // Load reports why through onError; either way this display's own layout is broken and
            // no other display's layout is an acceptable stand-in for it.
            return Load(path) is { } hit ? new LayoutResolution(hit, path, sig, Scaled: false) : null;
        }

        LayoutResolution? best = null; var bestScore = -1; DateTime bestWrite = DateTime.MinValue;
        foreach (var (key, rel) in _entries)
        {
            var path = Resolve(rel);
            if (!File.Exists(path)) continue;
            DisplaySignature candidate;
            try { candidate = DisplaySignature.Parse(key); } catch (FormatException) { continue; }
            var score = sig.Similarity(candidate);
            var write = File.GetLastWriteTimeUtc(path);
            if (score > bestScore || (score == bestScore && write > bestWrite))
            {
                if (Load(path) is not { } file) continue;
                bestScore = score; bestWrite = write;
                best = new LayoutResolution(LayoutScaler.Scale(file, candidate, sig), path, candidate, Scaled: true);
            }
        }
        return best;
    }

    private LayoutFile? TryLoad(string path)
    {
        LayoutFile file;
        try { file = LayoutFile.Load(path); }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            _onError?.Invoke($"layout {path} cannot be read: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        if (file.Version > MaxVersion)
        {
            _onError?.Invoke($"layout {path} is version {file.Version}; this build understands up to {MaxVersion}");
            return null;
        }
        return file;
    }

    private string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(_baseDir, p));

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var doc = JsonSerializer.Deserialize(File.ReadAllText(_storePath), LayoutStoreJsonContext.Default.LayoutStoreFile);
            _entries = new(doc?.Layouts ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { _entries = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        Directory.CreateDirectory(_baseDir);
        var tmp = _storePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(new LayoutStoreFile { Layouts = _entries }, LayoutStoreJsonContext.Default.LayoutStoreFile));
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch
        {
            // Same tidy-runtime-dir discipline as FrameState.Save/LayoutFile.Save (Phase 1 carry-over finding 10).
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

public sealed class LayoutStoreFile
{
    public Dictionary<string, string> Layouts { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LayoutStoreFile))]
internal partial class LayoutStoreJsonContext : JsonSerializerContext;
