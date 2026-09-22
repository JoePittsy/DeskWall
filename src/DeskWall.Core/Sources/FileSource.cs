using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Settings: path (required; %ENV% variables and the runtime: prefix expanded); parse = json | text | rss (default by extension: .json,
/// .xml/.rss/.atom => rss, else text); every (default 300 s: the cheap re-check of mtime, not the
/// latency - a FileSystemWatcher carries that and raises Changed within a debounce of the save);
/// unixTimeFields as for http. Publishes: json | text |
/// (for rss) title/link/items, plus modifiedAt (TimeValue), size (NumberValue), exists (BoolValue, always true).
/// NextDue returns now when the file's mtime changed since the last refresh, so the tick picks a change up on any wake.
/// A missing file throws (spec 3.2): the previous values stay published rather than the column blanking.</summary>
#pragma warning disable CS9113 // clock is kept for parity with SourceFactory.Create(def, clock) / FromDef; this source is driven by its watcher and the file's mtime, never by the clock.
public sealed class FileSource(string name, TimeSpan every, string path, string? parse, IReadOnlySet<string> unixTimeFields, IClock clock) : ISource, ISignalSource, IDisposable
#pragma warning restore CS9113
{
    /// <summary>The same 300 ms LayoutWatcher uses, for the same reason: one Ctrl+S raises three or
    /// four events (write, size, and for a save-by-rename a delete and a create), and each one
    /// reaching the daemon separately is a two-megabyte repaint for nothing.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>What `every` was before the watcher took over the latency, kept as the retry base
    /// after a failure. See <see cref="Interval"/>.</summary>
    private static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private System.Threading.Timer? _debounce;
    private FileSystemWatcher? _watcher;
    private DateTime _seenMtime;
    /// <summary>The watcher has raised Changed and no refresh has taken the new contents yet.
    /// Signalling only buys a wake: the tick that follows still asks the scheduler which sources
    /// are due, so without this the wake finds nothing to do and the save never reaches the
    /// screen. The mtime check below is nearly the same statement, but NTFS timestamps are coarse
    /// and a write in the same tick as the last refresh's mtime would slip through it.
    /// <para>It must go back to false in RefreshAsync. A source that is unconditionally due pins
    /// the daemon's wake at Scheduler.MinDelay - four ticks a second - which is the failure mode
    /// HardwareSource.NextDue's comment warns about.</para></summary>
    private volatile bool _pending;
    /// <summary>Set on the loop thread in Dispose, read on watcher and timer threads.</summary>
    private volatile bool _disposed;

    public string Name => name;
    public string Path { get; } = Paths.ExpandPath(path);

    /// <summary>Raised a debounce after the file last changed, on a pool thread.</summary>
    public event Action<ISource>? Changed;

    public static FileSource FromDef(SourceDef def, IClock clock)
    {
        var s = def.Settings;
        if (!s.TryGetValue("path", out var p) || string.IsNullOrWhiteSpace(p)) throw new ArgumentException($"file source '{def.Name}' needs settings.path");
        var unix = new HashSet<string>((s.TryGetValue("unixTimeFields", out var u) ? u : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        var src = new FileSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 300), p, s.TryGetValue("parse", out var mode) ? mode : null, unix, clock);
        src.StartWatching();
        return src;
    }

    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        if (_pending) return now;
        var mtime = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
        return mtime != _seenMtime ? now : lastRefresh.Value + every;
    }

    /// <summary>The scheduler asks this for one thing only: the base of the back-off after a
    /// failure. It is deliberately not `every`. While a source is failing the scheduler ignores
    /// NextDue, so the watcher cannot help it - a producer that deletes its file and writes the new
    /// one more than a debounce later is refused once and then not asked again until the back-off
    /// is up. Raising the healthy re-check to 300 s would otherwise have taken that retry from
    /// 30 s to 300 s as a side effect, which is not what raising it was for. An `every` shorter
    /// than 30 s is an explicit ask and still wins.</summary>
    public TimeSpan Interval(DateTimeOffset now) => every < RetryBase ? every : RetryBase;

    /// <summary>Watch the file's directory, filtered to its name. Idempotent and cheap to retry: a
    /// layout may name a file whose directory a producer has not created yet, and FileSystemWatcher
    /// throws from its constructor in that case. The mtime re-check in NextDue is what keeps such a
    /// source working meanwhile - and is also the belt for a network path or a container mount,
    /// which can raise no events at all.
    /// <para>FileSystemWatcher is AOT-safe and costs no dedicated thread while idle; events arrive
    /// on the thread pool, which is why everything below takes the lock.</para></summary>
    private void StartWatching()
    {
        if (_disposed) return;
        var dir = System.IO.Path.GetDirectoryName(Path);
        var file = System.IO.Path.GetFileName(Path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
        lock (_lock)
        {
            if (_watcher is not null || _disposed || !Directory.Exists(dir)) return;
            try
            {
                _debounce ??= new System.Threading.Timer(static s => ((FileSource)s!).Fire(), this, Timeout.Infinite, Timeout.Infinite);
                var w = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                w.Changed += OnFileEvent;
                w.Created += OnFileEvent;
                w.Deleted += OnFileEvent;
                w.Renamed += OnFileEvent;
                // A producer writing a large JSON can outrun the default 8 KB buffer; a lost event
                // here is a widget that shows yesterday's numbers until the 300 s re-check.
                w.InternalBufferSize = 65536;
                w.Error += (_, _) => Kick();   // buffer overflow: assume something changed
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception)
            {
                // A path the watcher will not take (a dead network share, a directory that vanished
                // between the check and the constructor) must not stop the source reading the file.
                _watcher = null;
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Kick();

    private void Kick()
    {
        if (_disposed) return;
        var t = _debounce;
        if (t is null) return;
        try { t.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }   // Dispose won the race; there is nothing left to wake
    }

    /// <summary>Nothing escapes a timer callback: an exception out of one takes the whole process
    /// down, which is the same reason EventBus.OnTimer swallows its handler's.</summary>
    private void Fire()
    {
        if (_disposed) return;
        // Set in the same branch that raises Changed, so one debounce governs both the wake and
        // the due-ness that wake depends on.
        _pending = true;
        try { Changed?.Invoke(this); }
        catch (Exception) { }
    }

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        // Idempotent, and the retry for the directory-that-appeared-later case. Here rather than in
        // NextDue, which runs several times a tick, because the retry costs a syscall.
        StartWatching();
        // Cleared before the read, not after: a save that lands mid-read would otherwise be marked
        // taken when what was published predates it. Clearing it at all is what stops an
        // unconditionally-due source pinning the daemon at Scheduler.MinDelay.
        _pending = false;
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Path))
        {
            // Spec 3.2: a source that cannot produce its values throws, so SourceSnapshot.Failed keeps
            // the previous ones. Publishing { exists: false } was a *successful* partial record that
            // installed itself over the last good values, so a producer writing its JSON by
            // delete-then-create blanked the whole column for a tick instead of holding the last list.
            // A layout that wants to say "file missing" does it in the component's fallback.
            _seenMtime = DateTime.MinValue;
            throw new FileNotFoundException($"file source '{name}': no file at {Path}", Path);
        }
        var info = new FileInfo(Path);
        _seenMtime = info.LastWriteTimeUtc;
        var text = File.ReadAllText(Path);
        var mode = parse ?? (info.Extension.ToLowerInvariant() switch { ".json" => "json", ".xml" or ".rss" or ".atom" => "rss", _ => "text" });
        switch (mode)
        {
            case "json": d["json"] = JsonValues.Parse(text, unixTimeFields); break;
            case "rss":
                foreach (var (k, v) in RssSource.ParseFeed(text, 50).Fields) d[k] = v;
                break;
            default: d["text"] = new TextValue(text); break;
        }
        d["exists"] = new BoolValue(true);
        d["modifiedAt"] = new TimeValue(new DateTimeOffset(info.LastWriteTimeUtc).ToLocalTime());
        d["size"] = new NumberValue(info.Length);
        return new(new RecordValue(d));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FileSystemWatcher? w;
        System.Threading.Timer? t;
        lock (_lock) { w = _watcher; _watcher = null; t = _debounce; _debounce = null; }
        if (w is not null)
        {
            // A directory that still holds a watcher keeps a 64 KB kernel buffer for the life of the
            // process, and the daemon builds a new set of sources on every layout and display change.
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        t?.Dispose();
    }
}
