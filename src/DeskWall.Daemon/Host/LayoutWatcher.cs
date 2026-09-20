using DeskWall.Core.Layout;

namespace DeskWall.Daemon.Host;

/// <summary>Watches the layout files named in the store (and the store itself) and calls back once,
/// 300 ms after the last event in a burst. Editors save by write-temp-then-rename, which can raise
/// three or four events for one Ctrl+S; the debounce collapses them into one re-render.
/// <para>
/// FileSystemWatcher can only filter by directory and wildcard, and layouts.json lives in the runtime
/// directory next to frame-state.json, which the daemon itself rewrites on every tick. A plain
/// "*.json in this directory" watch therefore feeds the daemon its own output: tick, write state,
/// wake, tick, at about two frames a second. So every event is checked against the exact set of files
/// we care about, and that set is snapshotted on the loop thread (Rescan) rather than read live off
/// the store from a pool thread.
/// </para>
/// FileSystemWatcher is AOT-safe and costs no dedicated thread while idle - events arrive on the
/// thread pool, so the callback given here must be thread-safe (posting a window message is).</summary>
internal sealed class LayoutWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly LayoutStore _store;
    private readonly Action _onChanged;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private volatile HashSet<string> _interesting = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LayoutWatcher(LayoutStore store, Action onChanged)
    {
        _store = store;
        _onChanged = onChanged;
        _timer = new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        Rescan();
    }

    /// <summary>Re-read which files matter and add a watcher for any directory not covered yet. The
    /// store changes when `deskwall layouts set` adds an entry, possibly pointing somewhere new, so
    /// the loop calls this after every reload. Cheap and idempotent. Loop thread only.</summary>
    public void Rescan()
    {
        if (_disposed) return;
        var paths = _store.WatchPaths.Select(Path.GetFullPath).ToList();
        _interesting = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || _watchers.ContainsKey(dir) || !Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Deleted += OnEvent;
            w.Renamed += OnRenamed;
            // An editor rewriting a big file can outrun the default 8 KB buffer, and a lost event is a
            // layout edit that never re-renders; take the 64 KB and treat an overflow as "something changed".
            w.InternalBufferSize = 65536;
            w.Error += OnError;
            w.EnableRaisingEvents = true;
            _watchers[dir] = w;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (_interesting.Contains(e.FullPath)) Kick();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Save-by-rename: the temp file we do not care about becomes the layout we do.
        if (_interesting.Contains(e.FullPath) || _interesting.Contains(e.OldFullPath)) Kick();
    }

    private void OnError(object sender, ErrorEventArgs e) => Kick();   // buffer overflow: assume something changed

    private void Kick()
    {
        if (_disposed) return;
        try { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Fire()
    {
        if (_disposed) return;
        _onChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers.Values)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        _timer.Dispose();
    }
}
