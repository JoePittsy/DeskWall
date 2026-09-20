using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Last known state of one source. Immutable; the registry swaps whole snapshots.</summary>
public sealed record SourceSnapshot(
    string Name,
    RecordValue? Values,
    DateTimeOffset? LastRefresh,
    string? LastError,
    int ConsecutiveFailures)
{
    public static SourceSnapshot Initial(string name) => new(name, null, null, null, 0);

    public SourceSnapshot Succeeded(RecordValue v, DateTimeOffset at)
        => this with { Values = v, LastRefresh = at, LastError = null, ConsecutiveFailures = 0 };

    public SourceSnapshot Failed(string error)
        => this with { LastError = error, ConsecutiveFailures = ConsecutiveFailures + 1 };
}

/// <summary>All snapshots, keyed by source name. Builds the value tree for resolution.</summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, SourceSnapshot> _snaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Every source's last good values, however old they are.</summary>
    public RecordValue Tree()
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _snaps.Values) if (s.Values is not null) d[s.Name] = s.Values;
        return new RecordValue(d);
    }

    /// <summary>As Tree(), but omits any source whose last refresh is older than <see cref="StaleAfter"/>
    /// of its own schedules. The schedule length is read back from the source's own NextDue, so a
    /// source with an irregular schedule (TimeSource's whole-minute boundary) is judged by the
    /// interval it actually asks for rather than one this class guesses.</summary>
    public RecordValue Tree(IReadOnlyList<ISource> sources, DateTimeOffset now)
    {
        if (StaleAfter <= 0) return Tree();
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            known.Add(s.Name);
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
