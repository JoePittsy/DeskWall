using DeskWall.Core.Layout;

namespace DeskWall.Daemon.Host;

/// <summary>Watches every directory that holds a layout file (and the store itself) and calls back
/// once, 300 ms after the last event in a burst. Editors save by write-temp-then-rename, which can
/// raise three or four events for one Ctrl+S; the debounce collapses them into one re-render.
/// FileSystemWatcher is AOT-safe and costs no dedicated thread while idle - the callback arrives on
/// the thread pool, so the callback given here must be thread-safe (the daemon posts a window
/// message, which is).</summary>
internal sealed class LayoutWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly LayoutStore _store;
    private readonly Action _onChanged;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LayoutWatcher(LayoutStore store, Action onChanged)
    {
        _store = store;
        _onChanged = onChanged;
        _timer = new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        Rescan();
    }

    /// <summary>Directories to watch come from the store, which changes when `layouts set` adds an
    /// entry pointing somewhere new. Cheap and idempotent; the loop calls it after every reload.</summary>
    public void Rescan()
    {
        if (_disposed) return;
        foreach (var path in _store.WatchPaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir) || _watchers.ContainsKey(dir) || !Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Deleted += OnEvent;
            w.Renamed += OnEvent;
            // An editor rewriting a big file can outrun the default 8 KB buffer; a lost event would
            // mean a layout edit that never re-renders, so take the 64 KB and the Error hook.
            w.InternalBufferSize = 65536;
            w.Error += OnError;
            w.EnableRaisingEvents = true;
            _watchers[dir] = w;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Kick();

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
