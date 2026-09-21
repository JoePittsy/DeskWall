using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;

namespace DeskWall.Designer.Model;

/// <summary>Runs the layout's sources inside the designer so panels show real values and the preview
/// renders real data. Re-created whenever the model's Sources list changes. Refreshes each source on
/// its own schedule with a single System.Threading.Timer; never more often than every 5 s.</summary>
public sealed class LiveSources : IDisposable
{
    private sealed class Entry
    {
        public required SourceDef Def;
        public ISource? Source;
        /// <summary>Kept so Dispose can unsubscribe: an in-flight HTTP fetch otherwise holds this
        /// instance, and through Updated the panel's whole visual tree, until it times out.</summary>
        public Action<ISource>? OnCompleted;
    }

    private readonly IClock _clock;
    private readonly List<Entry> _entries;
    private readonly SourceRegistry _registry = new();
    private readonly object _registryLock = new();
    private readonly Timer _timer;
    private int _ticking;
    private bool _disposed;

    /// <param name="create">Test seam: how a def becomes a source. Null means the real factory.</param>
    public LiveSources(IReadOnlyList<SourceDef> defs, Secrets secrets, IClock clock, Func<SourceDef, ISource>? create = null)
    {
        _clock = clock;
        _entries = new List<Entry>(defs.Count);
        foreach (var def in defs)
        {
            var entry = new Entry { Def = def };
            try
            {
                entry.Source = create is null ? SourceFactory.Create(def, clock, secrets) : create(def);
                if (entry.Source is AsyncSource async)
                {
                    entry.OnCompleted = _ => OnAsyncCompleted(entry);
                    async.Completed += entry.OnCompleted;
                }
            }
            catch (Exception ex)
            {
                lock (_registryLock) _registry.Set(SourceSnapshot.Initial(def.Name).Failed(ex.Message, clock.Now));
            }
            _entries.Add(entry);
        }
        // Fire an immediate tick, then every 5 s. Each source's own NextDue still governs whether it actually runs.
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public RecordValue Tree() { lock (_registryLock) return _registry.Tree(); }

    public IReadOnlyList<SourceSnapshot> Snapshots
    {
        get { lock (_registryLock) return _entries.Select(e => _registry.Get(e.Def.Name)).ToList(); }
    }

    /// <summary>Raised (not on the UI thread) after any refresh, successful or not. Subscribers marshal.</summary>
    public event Action? Updated;

    public async Task RefreshNowAsync(string name)
    {
        var entry = _entries.FirstOrDefault(e => string.Equals(e.Def.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no source named '{name}'", nameof(name));
        await RefreshEntryAsync(entry).ConfigureAwait(false);
    }

    private void Tick()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var now = _clock.Now;
            foreach (var entry in _entries)
            {
                if (entry.Source is null) continue;
                SourceSnapshot snap;
                lock (_registryLock) snap = _registry.Get(entry.Def.Name);
                if (entry.Source.NextDue(snap.LastRefresh, now) <= now)
                    _ = RefreshEntryAsync(entry);
            }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void OnAsyncCompleted(Entry entry)
    {
        if (_disposed) return;
        // An overrun refresh finished late; harvest it now (AsyncSource.RefreshAsync returns the
        // finished result immediately once Completed has fired) rather than waiting for the next tick.
        _ = RefreshEntryAsync(entry);
    }

    private async Task RefreshEntryAsync(Entry entry)
    {
        // Checked here and again before Updated: a fetch that lands after Dispose would otherwise
        // call Dispatcher.Invoke on a window that has closed, or repaint a panel that has already
        // replaced this instance.
        if (_disposed) return;
        if (entry.Source is null)
        {
            lock (_registryLock) _registry.Set(_registry.Get(entry.Def.Name));
            if (!_disposed) Updated?.Invoke();
            return;
        }
        try
        {
            var v = await entry.Source.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_registryLock) _registry.Set(_registry.Get(entry.Def.Name).Succeeded(v, _clock.Now));
        }
        catch (Exception ex)
        {
            lock (_registryLock) _registry.Set(_registry.Get(entry.Def.Name).Failed(ex.Message, _clock.Now));
        }
        if (!_disposed) Updated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        foreach (var e in _entries)
            if (e.Source is AsyncSource a && e.OnCompleted is { } handler) a.Completed -= handler;
        // The designer builds a new LiveSources on every edit to the Sources list, so a source that
        // holds a timer or a native library (`hardware` holds both) would leak one per edit.
        SourceFactory.DisposeAll(_entries.Select(e => e.Source).OfType<ISource>());
    }
}
