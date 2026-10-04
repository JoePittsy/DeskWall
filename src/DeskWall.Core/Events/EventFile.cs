namespace DeskWall.Core.Events;

/// <summary>The daemon's side of events.json: restore at start, save from the bus, and read back
/// a Forget. Issue #17: the designer's Forget rewrites the file without the record, and a daemon
/// that only ever wrote the file put the record straight back on its next save.
/// <para>A Forget is told apart from everything else by comparing against what this instance
/// last wrote or restored. A name that was there and is now missing was removed by someone else,
/// and the bus drops it. A name the bus holds that was never written - a provider that arrived
/// since the last save - is left alone, because its absence says nothing. Records the file gains
/// are ignored: the bus is the source of truth for what a producer sent, and nobody but the
/// daemon adds to the file.</para>
/// <para>Not thread safe: the daemon calls every member on its tick thread, which is also what
/// keeps two saves from racing for the file. The bus it feeds is synchronised.</para></summary>
public sealed class EventFile(EventBus bus)
{
    /// <summary>Each name as last written or restored, with the receive time of the record that
    /// went to disk.</summary>
    private Dictionary<string, DateTimeOffset> _written = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Put the remembered records back into the bus.</summary>
    public void Restore()
    {
        var records = EventStore.Load();
        bus.Restore(records);
        _written = Written(records);
    }

    /// <summary>Write the bus's records. Throws what <see cref="EventStore.Save"/> throws, and on
    /// a throw remembers nothing new, so the next Reconcile still compares against the file that
    /// is actually there.</summary>
    public void Save()
    {
        var records = bus.Providers.Values.ToList();
        EventStore.Save(records);
        _written = Written(records);
    }

    /// <summary>Read the file back and drop from the bus every provider it has lost since this
    /// instance last wrote it. Returns the names dropped, empty when there were none. A file that
    /// is missing, torn or unreadable forgets nothing: losing every provider because a read hit a
    /// half-edited file is the failure this guards against hardest.</summary>
    public IReadOnlyList<string> Reconcile()
    {
        // The daemon's own save raises the same watcher event as a Forget, so this reads the file
        // back after every save too: a couple of kilobytes, at most once per save. Not skipped by
        // write time, which NTFS can leave unchanged across two writes in the same clock tick.
        if (EventStore.TryLoad() is not { } onDisk) return [];
        var kept = new HashSet<string>(onDisk.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var current = bus.Providers;
        var forgotten = new List<string>();
        foreach (var (name, receivedAt) in _written.Where(w => !kept.Contains(w.Key)).OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
        {
            // A producer that sent again after the Forget was written is a producer the user
            // still has; dropping that event would lose something sent after the click.
            if (current.TryGetValue(name, out var r) && r.ReceivedAt != receivedAt) continue;
            bus.Forget(name);
            forgotten.Add(name);
        }
        foreach (var name in _written.Keys.Where(n => !kept.Contains(n)).ToList()) _written.Remove(name);
        return forgotten;
    }

    private static Dictionary<string, DateTimeOffset> Written(IEnumerable<ProviderRecord> records)
    {
        var d = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records) d[r.Name] = r.ReceivedAt;
        return d;
    }
}
