using DeskWall.Core.Layout;

namespace DeskWall.Core.Sources;

public static class SourceFactory
{
    public static ISource Create(SourceDef def, IClock clock) => Create(def, clock, Secrets.Default());

    public static ISource Create(SourceDef def, IClock clock, Secrets secrets)
    {
        var every = TimeSpan.FromSeconds(def.EverySeconds ?? DefaultEvery(def.Type));
        return def.Type.ToLowerInvariant() switch
        {
            "time" => new TimeSource(def.Name, clock),
            "disks" => new DisksSource(def.Name, every),
            "system" => SystemSource.FromDef(def, clock),
            "file" => FileSource.FromDef(def, clock),
            "http" => HttpSource.FromDef(def, clock, secrets),
            "rss" => RssSource.FromDef(def, secrets),
            "command" => CommandSource.FromDef(def, clock, secrets),
            "hardware" => Hardware.HardwareSource.FromDef(def),
            "audio" => Audio.AudioSource.FromDef(def),
            _ => throw new NotSupportedException($"source type '{def.Type}' (source '{def.Name}')"),
        };
    }

    /// <summary>Let go of every source in a set that holds resources (a timer, a native library).
    /// Whoever owns a set of sources calls this when it replaces the set and when it shuts down:
    /// the daemon rebuilds the whole set on every layout change and every display change, and the
    /// designer rebuilds it on every edit to the Sources list, so anything not disposed here leaks
    /// once per edit for the life of the process.
    /// <para>A source that throws from its own Dispose must not stop the others and must not take
    /// the daemon down on the way out, so each one is swallowed.</para></summary>
    public static void DisposeAll(IEnumerable<ISource>? sources)
    {
        if (sources is null) return;
        foreach (var s in sources)
        {
            try { (s as IDisposable)?.Dispose(); }
            catch (Exception) { }
        }
    }

    private static int DefaultEvery(string type) => type.ToLowerInvariant() switch
    {
        "disks" => 300,
        "system" => 900,
        "hardware" => 60,
        _ => 600,
    };
}
