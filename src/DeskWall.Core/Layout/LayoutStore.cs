using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Display;

namespace DeskWall.Core.Layout;

public sealed record LayoutResolution(LayoutFile Layout, string SourcePath, DisplaySignature SourceSignature, bool Scaled);

/// <summary>layouts.json in the runtime dir: { "layouts": { "&lt;signature key&gt;": "&lt;layout path&gt;" } }.
/// Layout paths are absolute or relative to the runtime dir.</summary>
public sealed class LayoutStore
{
    private readonly string _storePath;
    private readonly string _baseDir;
    private Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public LayoutStore(string storePath)
    {
        _storePath = Path.GetFullPath(storePath);
        _baseDir = Path.GetDirectoryName(_storePath)!;
        Load();
    }

    public static LayoutStore Default() => new(Paths.InRuntime("layouts.json"));

    public IReadOnlyDictionary<string, string> Entries => _entries.ToDictionary(kv => kv.Key, kv => Resolve(kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file the daemon must watch: the store itself and all layout files.</summary>
    public IReadOnlyList<string> WatchPaths => [_storePath, .. _entries.Values.Select(Resolve)];

    public void Set(DisplaySignature sig, string layoutPath) { _entries[sig.Key] = Path.GetFullPath(layoutPath); Save(); }

    public void Remove(DisplaySignature sig) { if (_entries.Remove(sig.Key)) Save(); }

    /// <summary>Exact match, else the closest by Similarity (ties: most recently written file), scaled to sig. Null when the store is empty.</summary>
    public LayoutResolution? Resolve(DisplaySignature sig)
    {
        if (_entries.TryGetValue(sig.Key, out var exact) && File.Exists(Resolve(exact)))
            return new LayoutResolution(LayoutFile.Load(Resolve(exact)), Resolve(exact), sig, Scaled: false);

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
                bestScore = score; bestWrite = write;
                best = new LayoutResolution(LayoutScaler.Scale(LayoutFile.Load(path), candidate, sig), path, candidate, Scaled: true);
            }
        }
        return best;
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
