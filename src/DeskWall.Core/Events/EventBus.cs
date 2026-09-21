using DeskWall.Core.Sources;

namespace DeskWall.Core.Events;

/// <summary>One line the bus was offered, and what became of it. Kept whether accepted or not:
/// "my script sends events and nothing happens" has four causes (wrong source name, malformed
/// envelope, daemon not running, nothing bound to the value) and without the rejected lines and
/// their reasons the user cannot tell them apart.</summary>
/// <param name="Line">The line as received; empty for an in-process publish, which never had one.</param>
public sealed record EventLogEntry(DateTimeOffset At, string? Source, bool Accepted, string? Reason, string Line);

/// <summary>Accepts events, owns the provider records, coalesces the repaints they ask for and
/// keeps a diagnostics ring. Lives in Core so the designer can run one too; the pipe that feeds
/// it lives in the daemon, because two processes cannot own one pipe name.
/// <para>Thread safety: one lock covers the providers, the ring and the pending wake. The pipe
/// thread writes and the tick thread reads, so <see cref="Providers"/> and <see cref="Recent"/>
/// hand back snapshots rather than the live collections. Handlers are invoked outside the lock:
/// <see cref="WakeRequested"/> posts to the daemon's window, and holding the lock across that
/// would invite a deadlock with a tick that is reading providers.</para></summary>
public sealed class EventBus : IDisposable
{
    public const int RingSize = 50;

    /// <summary>A tick re-encodes and writes about two megabytes, so a slider drag that sends
    /// fifty events must not become fifty repaints.</summary>
    public static readonly TimeSpan DefaultCoalesce = TimeSpan.FromMilliseconds(400);

    private readonly IClock _clock;
    private readonly TimeSpan _coalesce;
    private readonly bool _autoWake;
    private readonly object _lock = new();
    private readonly Dictionary<string, ProviderRecord> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<EventLogEntry> _ring = new(RingSize);
    private DateTimeOffset? _wakeDue;
    private Timer? _timer;
    private bool _disposed;

    public EventBus(IClock clock, TimeSpan coalesce, bool autoWake = true)
    {
        _clock = clock;
        _coalesce = coalesce < TimeSpan.Zero ? TimeSpan.Zero : coalesce;
        _autoWake = autoWake;
    }

    /// <summary>Coalesced. Never raised on the caller's thread when autoWake is true.</summary>
    public event Action? WakeRequested;

    /// <summary>Raised for every accepted event, uncoalesced, for the designer's panel.</summary>
    public event Action<string>? ProviderChanged;

    public IReadOnlyDictionary<string, ProviderRecord> Providers
    {
        get { lock (_lock) return new Dictionary<string, ProviderRecord>(_providers, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>The last <see cref="RingSize"/> lines offered, oldest first.</summary>
    public IReadOnlyList<EventLogEntry> Recent
    {
        get { lock (_lock) return _ring.ToArray(); }
    }

    public bool Publish(string line)
    {
        var (e, why) = EventEnvelopeParser.Parse(line);
        if (e is null)
        {
            lock (_lock) Log(new EventLogEntry(_clock.Now, null, false, why, line ?? ""));
            return false;
        }
        return Apply(e, line);
    }

    public bool Publish(EventEnvelope e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Apply(e, "");
    }

    public void Forget(string provider)
    {
        // Silent about a name it has never heard of: the designer's Forget button and a shutdown
        // save can race, and a throw there would be noise about nothing.
        lock (_lock) _providers.Remove(provider);
    }

    /// <summary>Put remembered records back at start. Not an event: nothing is logged and nothing
    /// wakes, because the tick that follows start paints them anyway.</summary>
    public void Restore(IEnumerable<ProviderRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_lock)
            foreach (var r in records) _providers[r.Name] = r;
    }

    /// <summary>Fires a due coalesced wake. Public so tests drive the clock instead of sleeping.</summary>
    public bool PumpWake(DateTimeOffset now) => PumpWake(now, force: false);

    private bool PumpWake(DateTimeOffset now, bool force)
    {
        Action? handler;
        lock (_lock)
        {
            if (_wakeDue is not { } due) return false;
            if (!force && now < due) return false;
            _wakeDue = null;
            handler = WakeRequested;
        }
        handler?.Invoke();
        return true;
    }

    private bool Apply(EventEnvelope e, string line)
    {
        bool arm;
        lock (_lock)
        {
            var now = _clock.Now;
            var current = _providers.TryGetValue(e.Source, out var r) ? r : ProviderRecord.Empty(e.Source, now);
            // A producer that re-sends on a heartbeat costs a repaint for nothing; an id it
            // repeats is its own statement that this is the same fact as last time.
            if (e.Id is not null && current.Id is not null && string.Equals(current.Id, e.Id, StringComparison.Ordinal))
            {
                Log(new EventLogEntry(now, e.Source, false, $"duplicate id \"{e.Id}\"", line));
                return false;
            }
            _providers[e.Source] = current.Apply(e, now);
            Log(new EventLogEntry(now, e.Source, true, null, line));
            // The first event of a burst sets the deadline and the rest ride on it, so the wake is
            // trailing: the final state always lands, at most _coalesce after the burst started.
            arm = e.Wake && _wakeDue is null;
            if (arm) _wakeDue = now + _coalesce;
        }
        if (arm) ArmTimer();
        ProviderChanged?.Invoke(e.Source);
        return true;
    }

    /// <summary>Caller holds the lock.</summary>
    private void Log(EventLogEntry entry)
    {
        if (_ring.Count == RingSize) _ring.Dequeue();
        _ring.Enqueue(entry);
    }

    private void ArmTimer()
    {
        if (!_autoWake) return;
        lock (_lock)
        {
            if (_disposed) return;
            _timer ??= new Timer(static s => ((EventBus)s!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_coalesce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Nothing escapes: an exception out of a timer callback takes the whole process
    /// down, the same reason HardwareSource.SampleOnce swallows its reader's. A wake handler is
    /// the daemon's window post, and a window that has just been destroyed must cost one repaint
    /// and nothing more. force: the timer fires at the deadline, and Windows timer resolution can
    /// put the clock a millisecond short of it, which would otherwise lose the wake entirely.</summary>
    private void OnTimer()
    {
        try
        {
            PumpWake(_clock.Now, force: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }
}
