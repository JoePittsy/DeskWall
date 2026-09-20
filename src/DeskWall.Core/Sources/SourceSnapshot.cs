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

    public IReadOnlyCollection<SourceSnapshot> All => _snaps.Values;

    public SourceSnapshot Get(string name) => _snaps.TryGetValue(name, out var s) ? s : SourceSnapshot.Initial(name);

    public void Set(SourceSnapshot s) => _snaps[s.Name] = s;

    public RecordValue Tree()
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _snaps.Values) if (s.Values is not null) d[s.Name] = s.Values;
        return new RecordValue(d);
    }
}
