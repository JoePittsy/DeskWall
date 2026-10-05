using DeskWall.Core.Events;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;

namespace DeskWall.Designer.Model;

/// <summary>Runs the layout's sources inside the designer so panels show real values and the preview
/// renders real data. One instance lives as long as the window: <see cref="Update"/> changes the set
/// in place, keeping every running source whose identity (<see cref="Identity"/>: name, type,
/// schedule and settings) is unchanged, whatever order the new list has. Refreshes each source on
/// its own schedule with a single System.Threading.Timer; never more often than every 5 s.</summary>
public sealed class LiveSources : IDisposable
{
    private sealed class Entry
    {
        public required SourceDef Def;
        public required string Identity;
        public ISource? Source;
        /// <summary>The one extra hardware refresh just after its second sample (<see cref="WarmUp"/>).</summary>
        public Timer? Warm;
        /// <summary>Taken out of the set: a refresh still in flight must not write over its replacement.</summary>
        public volatile bool Retired;
        private readonly object _answerLock = new();
        private TaskCompletionSource? _answer;

        /// <summary>Completes the next time this entry records an answer (values or a failure), or is
        /// retired. What RefreshNowAsync waits on when its own refresh came back "pending".</summary>
        public Task NextAnswer()
        {
            lock (_answerLock) return (_answer ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        public void Answered()
        {
            TaskCompletionSource? t;
            lock (_answerLock) { t = _answer; _answer = null; }
            t?.TrySetResult();
        }
    }

    private readonly IClock _clock;
    private readonly Secrets _secrets;
    private readonly Func<SourceDef, ISource>? _create;
    /// <summary>Replaced whole, never mutated: the timer thread iterates whichever array it read.</summary>
    private volatile Entry[] _entries;
    private readonly SourceRegistry _registry = new();
    private readonly object _registryLock = new();
    private readonly object _updateLock = new();
    private readonly Timer _timer;
    /// <summary>The bus this instance signals. Owned (and disposed) only when the caller did not
    /// supply one.</summary>
    private readonly EventBus _bus;
    private readonly bool _ownsBus;
    /// <summary>Kept so a retired source and Dispose can unsubscribe: a handler left on a source keeps
    /// this instance - and through Updated the panels - alive.</summary>
    private readonly Action<ISource> _onSignal;
    private readonly Action _onWake;
    private int _ticking;
    private bool _disposed;

    /// <param name="create">Test seam: how a def becomes a source. Null means the real factory.</param>
    /// <param name="bus">The bus to signal through. Null means one of this instance's own, which is
    /// what the designer uses: it does not own the pipe, so its bus carries only in-process signals
    /// and the providers panel's test events.</param>
    public LiveSources(IReadOnlyList<SourceDef> defs, Secrets secrets, IClock clock, Func<SourceDef, ISource>? create = null, EventBus? bus = null)
    {
        ArgumentNullException.ThrowIfNull(defs);
        _clock = clock;
        _secrets = secrets;
        _create = create;
        _ownsBus = bus is null;
        _bus = bus ?? new EventBus(clock, EventBus.DefaultCoalesce);
        _onSignal = SourceSignals.To(_bus);
        _onWake = OnBusWake;
        _bus.WakeRequested += _onWake;
        _entries = defs.Select(Create).ToArray();
        // Fire an immediate tick, then every 5 s. Each source's own NextDue still governs whether it actually runs.
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>When the extra hardware refresh runs: just after the sampler's second reading.</summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(12);

    /// <summary>What makes two defs the same running source: name (case-insensitive), type, schedule
    /// and settings (in key order). Order in the list is not part of it.</summary>
    public static string Identity(SourceDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return string.Join("|", def.Name.ToLowerInvariant(), def.Type.ToLowerInvariant(), def.EverySeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            string.Join(",", def.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)));
    }

    private Entry Create(SourceDef def)
    {
        var entry = new Entry { Def = def, Identity = Identity(def) };
        try
        {
            entry.Source = _create is null ? SourceFactory.Create(def, _clock, _secrets) : _create(def);
            // The one rule, same as the daemon's: anything that knows it changed signals the
            // bus by name and the coalesced wake refreshes whatever is then due.
            if (entry.Source is ISignalSource sig) sig.Changed += _onSignal;
        }
        catch (Exception ex)
        {
            lock (_registryLock) _registry.Set(SourceSnapshot.Initial(def.Name).Failed(ex.Message, _clock.Now));
        }
        // A hardware source has no CPU load until its second sample (10 s in), and then publishes on
        // the whole minute, so a dial dropped in the designer, and "CPU load" in the Data panel,
        // would stay blank for up to a minute. One extra refresh just after that sample fills them
        // in. The designer only: the daemon's schedule is its own.
        if (entry.Source is not null && string.Equals(def.Type, "hardware", StringComparison.OrdinalIgnoreCase))
            entry.Warm = new Timer(_ => _ = RefreshEntryAsync(entry), null, WarmUp, Timeout.InfiniteTimeSpan);
        return entry;
    }

    /// <summary>Change the running set to <paramref name="defs"/>. A source whose
    /// <see cref="Identity"/> is already running keeps running, untouched (a depth change reorders
    /// the list and must not restart the hardware sampler). A new or changed one starts and is read at
    /// once; until that first read lands the tree keeps what the one it replaced last published under
    /// the same name. A name no longer in the list stops being published. True when anything changed.</summary>
    public bool Update(IReadOnlyList<SourceDef> defs)
    {
        ArgumentNullException.ThrowIfNull(defs);
        List<Entry> fresh = [], gone;
        lock (_updateLock)
        {
            if (_disposed) return false;
            var old = _entries;
            var kept = new HashSet<Entry>(ReferenceEqualityComparer.Instance);
            var next = new List<Entry>(defs.Count);
            foreach (var def in defs)
            {
                var id = Identity(def);
                var match = old.FirstOrDefault(e => e.Identity == id && !kept.Contains(e));
                if (match is not null) { kept.Add(match); next.Add(match); continue; }
                var entry = Create(def);
                next.Add(entry);
                fresh.Add(entry);
            }
            gone = old.Where(e => !kept.Contains(e)).ToList();
            _entries = next.ToArray();
            // A reorder alone changes nothing anyone can see: no event, no redraw.
            if (fresh.Count == 0 && gone.Count == 0) return false;
            var names = next.Select(e => e.Def.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_registryLock)
                foreach (var e in gone.Where(e => !names.Contains(e.Def.Name))) _registry.Remove(e.Def.Name);
        }
        Retire(gone);
        foreach (var e in fresh) _ = RefreshEntryAsync(e);
        if (!_disposed) Updated?.Invoke();
        return true;
    }

    private void Retire(IEnumerable<Entry> entries)
    {
        var list = entries.ToList();
        foreach (var e in list)
        {
            e.Retired = true;
            e.Answered();   // nobody waits on a source that has left the set
            e.Warm?.Dispose();
            if (e.Source is ISignalSource sig) sig.Changed -= _onSignal;
        }
        // A source that holds a timer or a native library (`hardware` holds both) would leak one per change.
        SourceFactory.DisposeAll(list.Select(e => e.Source).OfType<ISource>());
    }

    public RecordValue Tree() { lock (_registryLock) return _registry.Tree(); }

    /// <summary>Publish the pushed providers the designer knows about, so the binding picker
    /// offers them and the preview draws a component bound to one. Replaces the whole set: a
    /// provider the user has just forgotten must stop being published.
    /// <para>The designer does not own the pipe (the daemon does), so these come from the
    /// remembered records on disk and from the panel's test events.</para></summary>
    public void SetProviders(IEnumerable<ProviderRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var now = _clock.Now;
        lock (_registryLock)
        {
            var wanted = new HashSet<string>(records.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _registry.ProviderNames.Where(n => !wanted.Contains(n)).ToList())
                _registry.RemoveProvider(gone);
            foreach (var r in records) _registry.SetProvider(r.Name, r.ToValues(now));
        }
        if (!_disposed) Updated?.Invoke();
    }

    public IReadOnlyList<SourceSnapshot> Snapshots
    {
        get { var entries = _entries; lock (_registryLock) return entries.Select(e => _registry.Get(e.Def.Name)).ToList(); }
    }

    /// <summary>Raised (not on the UI thread) after any refresh, successful or not, and after an
    /// <see cref="Update"/> that changed the set. Subscribers marshal.</summary>
    public event Action? Updated;

    public async Task RefreshNowAsync(string name)
    {
        var entry = _entries.FirstOrDefault(e => string.Equals(e.Def.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no source named '{name}'", nameof(name));
        // Taken before the refresh, so an answer recorded while it runs is not missed. An async
        // source answers "pending" while a fetch is in flight - one this call started, or the first
        // one the constructor's tick started (#20) - and its landing is harvested by the bus wake.
        // Returning at once then made Refresh look like it did nothing and handed the caller an
        // empty snapshot; it returns with an answer instead.
        var answered = entry.NextAnswer();
        await RefreshEntryAsync(entry).ConfigureAwait(false);
        await answered.ConfigureAwait(false);
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
                if (entry.Source is null or AsyncSource { Fetching: true }) continue;   // its landing signals
                SourceSnapshot snap;
                lock (_registryLock) snap = _registry.Get(entry.Def.Name);
                if (entry.Source.NextDue(snap.LastRefresh, now) <= now)
                    _ = RefreshEntryAsync(entry);
            }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>A coalesced batch of signals. Ticking, rather than refreshing one named entry, is
    /// what makes this the same rule the daemon runs: the signal is a claim of due-ness and the
    /// source's own NextDue is what decides. AsyncSource answers "now" while an overrun fetch is
    /// waiting to be harvested, so a late landing is still picked up at once.</summary>
    private void OnBusWake()
    {
        if (_disposed) return;
        Tick();
    }

    private async Task RefreshEntryAsync(Entry entry)
    {
        // Checked here and again before Updated: a fetch that lands after Dispose would otherwise
        // call Dispatcher.Invoke on a window that has closed, or repaint a panel that has already
        // replaced this instance. A retired entry's late answer would overwrite its replacement's.
        if (_disposed || entry.Retired) { entry.Answered(); return; }
        if (entry.Source is null)
        {
            lock (_registryLock) _registry.Set(_registry.Get(entry.Def.Name));
            entry.Answered();
            if (!_disposed) Updated?.Invoke();
            return;
        }
        try
        {
            var v = await entry.Source.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            if (entry.Retired) { entry.Answered(); return; }
            lock (_registryLock) _registry.Set(_registry.Get(entry.Def.Name).Succeeded(v, _clock.Now));
        }
        // Still fetching: the panel keeps what it shows, and the landing's Changed brings it back here.
        catch (SourcePendingException) { return; }
        catch (Exception ex)
        {
            if (entry.Retired) { entry.Answered(); return; }
            lock (_registryLock) _registry.Set(_registry.Get(entry.Def.Name).Failed(ex.Message, _clock.Now));
        }
        entry.Answered();
        if (!_disposed) Updated?.Invoke();
    }

    public void Dispose()
    {
        lock (_updateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
        _bus.WakeRequested -= _onWake;
        Retire(_entries);
        if (_ownsBus) _bus.Dispose();
    }
}
