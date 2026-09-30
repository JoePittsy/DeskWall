using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>The value tree the last <see cref="SourceRegistry.Tree()"/> call built, for a source whose
/// settings name another source's value (time's <c>sunrise: weather.json.daily.sunrise[0]</c>).
/// A source has no registry to ask, and the registry's callers are the tick and the designer, so the
/// registry leaves its latest answer here. Reading it at refresh sees the tree of the previous tick:
/// one tick behind, which for a value that changes once a day is nothing.
/// <para>Process-wide. The daemon and the designer each run one registry; were there two, the later
/// caller wins, which only matters if they disagree about a source both declare.</para></summary>
public static class SourceTree
{
    private static RecordValue? _latest;

    public static RecordValue? Latest
    {
        get => Volatile.Read(ref _latest);
        internal set => Volatile.Write(ref _latest, value);
    }
}
