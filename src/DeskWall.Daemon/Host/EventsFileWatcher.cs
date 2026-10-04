namespace DeskWall.Daemon.Host;

/// <summary>Watches events.json and calls back once, 300 ms after the last event in a burst, so a
/// Forget written by the designer reaches the daemon while it runs (issue #17). One file, by exact
/// name: the runtime directory also holds frame-state.json, which every tick rewrites, and the
/// save's own scratch file (events.json.&lt;pid&gt;.tmp) does not match the filter either.
/// <para>The daemon's own save raises it too. That is deliberate rather than filtered out here:
/// the callback only posts a wake, and the loop reads the file and ticks only if something was
/// actually forgotten. Events arrive on the thread pool, so the callback must be thread safe
/// (posting a window message is).</para></summary>
public sealed class EventsFileWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly Action _onChanged;
    private readonly System.Threading.Timer _timer;
    private readonly FileSystemWatcher? _watcher;
    private volatile bool _disposed;

    public EventsFileWatcher(string path, Action onChanged)
    {
        _onChanged = onChanged;
        _timer = new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => Kick();
        _watcher.Created += (_, _) => Kick();
        // Both writers save by write-temp-then-rename, so the rename is the event that matters.
        _watcher.Renamed += (_, _) => Kick();
        _watcher.Error += (_, _) => Kick();   // buffer overflow: assume something changed
        _watcher.EnableRaisingEvents = true;
    }

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
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        _timer.Dispose();
    }
}
