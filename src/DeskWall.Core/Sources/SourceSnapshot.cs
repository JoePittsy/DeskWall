using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Last known state of one source. Immutable; the registry swaps whole snapshots.</summary>
/// <param name="LastRefresh">When the source last produced values; staleness is judged from this.</param>
/// <param name="LastAttempt">When the source last ran at all, successfully or not. The scheduler
/// schedules a failing source from this: LastRefresh alone leaves it permanently due (finding 1).</param>
public sealed record SourceSnapshot(
    string Name,
    RecordValue? Values,
    DateTimeOffset? LastRefresh,
    DateTimeOffset? LastAttempt,
    string? LastError,
    int ConsecutiveFailures)
{
    public static SourceSnapshot Initial(string name) => new(name, null, null, null, null, 0);

    public SourceSnapshot Succeeded(RecordValue v, DateTimeOffset at)
        => this with { Values = v, LastRefresh = at, LastAttempt = at, LastError = null, ConsecutiveFailures = 0 };

    public SourceSnapshot Failed(string error, DateTimeOffset at)
        => this with { LastAttempt = at, LastError = error, ConsecutiveFailures = ConsecutiveFailures + 1 };
}

/// <summary>All snapshots, keyed by source name. Builds the value tree for resolution.</summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, SourceSnapshot> _snaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Value> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Spec 3.2 staleness: after this many missed schedules a source's last good values stop
    /// being published, so bound components fall back instead of showing a frozen number forever.
    /// 0 (the default) keeps the Phase 1 behaviour of publishing stale values indefinitely.</summary>
    public int StaleAfter { get; set; }

    /// <summary>Raised when a source crosses the staleness line: (name, stale). Once per transition,
    /// not once per tick. The daemon turns it into one WARN going stale and one INFO coming back.</summary>
    public event Action<string, bool>? StaleChanged;

    public IReadOnlyCollection<SourceSnapshot> All => _snaps.Values;

    public SourceSnapshot Get(string name) => _snaps.TryGetValue(name, out var s) ? s : SourceSnapshot.Initial(name);

    public void Set(SourceSnapshot s) => _snaps[s.Name] = s;

    /// <summary>The pushed providers currently published. The caller reports a clash with a
    /// layout source from this; the registry does not throw over one.</summary>
    public IReadOnlyCollection<string> ProviderNames => _providers.Keys;

    /// <summary>Publish a pushed provider's values under <paramref name="name"/>. Spec section 3:
    /// there is no event source type, because a source declaration exists to tell the daemon what
    /// to go and do and a pushed provider needs none of that. It is one more entry in the same
    /// flat map, so a layout binds build.data.status with nothing declared anywhere.
    /// <para>Called from the tick thread, like every other method here; the bus, not the registry,
    /// is the thing the pipe thread touches.</para></summary>
    public void SetProvider(string name, RecordValue values) => _providers[name] = values;

    /// <summary>Silent about a name it does not have: Forget can race a save.</summary>
    public void RemoveProvider(string name) => _providers.Remove(name);

    /// <summary>Every source's last good values, however old they are.</summary>
    public RecordValue Tree()
    {
        var d = ProviderFields(null);
        foreach (var s in _snaps.Values) if (s.Values is not null) d[s.Name] = s.Values;
        return new RecordValue(d);
    }

    /// <summary>Providers first, so a layout source written over the top wins the name outright
    /// (spec section 3: they are not merged). A declared source owns its name even before its
    /// first refresh - letting the provider through for one tick and swapping it out on the next
    /// would flash on the wallpaper.</summary>
    private Dictionary<string, Value> ProviderFields(HashSet<string>? declared)
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, v) in _providers)
            if (!_snaps.ContainsKey(name) && declared?.Contains(name) != true) d[name] = v;
        return d;
    }

    /// <summary>As Tree(), but omits any source whose last refresh is older than <see cref="StaleAfter"/>
    /// of its own schedules. The schedule length is read back from the source's own NextDue, so a
    /// source with an irregular schedule (TimeSource's whole-minute boundary) is judged by the
    /// interval it actually asks for rather than one this class guesses.</summary>
    public RecordValue Tree(IReadOnlyList<ISource> sources, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources) known.Add(s.Name);
        // A pushed provider has no schedule, so no staleness rule applies to it either way; it is
        // added here and then, below, any layout source of the same name takes the name back.
        // (Spec section 5's expectEvery, which would give a provider a schedule to fall behind,
        // is phase 2.)
        var d = ProviderFields(known);
        if (StaleAfter <= 0)
        {
            foreach (var s in _snaps.Values) if (s.Values is not null) d[s.Name] = s.Values;
            return new RecordValue(d);
        }
        foreach (var s in sources)
        {
            var snap = Get(s.Name);
            if (snap.Values is null) continue;
            if (IsStale(s, snap, now))
            {
                if (_stale.Add(s.Name)) StaleChanged?.Invoke(s.Name, true);
                continue;
            }
            if (_stale.Remove(s.Name)) StaleChanged?.Invoke(s.Name, false);
            d[s.Name] = snap.Values;
        }
        // A snapshot whose source the caller did not list is on no schedule we know: publish it unjudged.
        foreach (var snap in _snaps.Values)
            if (snap.Values is not null && !known.Contains(snap.Name)) d[snap.Name] = snap.Values;
        return new RecordValue(d);
    }

    private bool IsStale(ISource s, SourceSnapshot snap, DateTimeOffset now)
    {
        if (snap.LastRefresh is not { } last) return false;
        var every = s.NextDue(last, now) - last;
        if (every <= TimeSpan.Zero) return false;   // always-due source: it can never fall behind
        return last + StaleAfter * every < now;
    }
}
