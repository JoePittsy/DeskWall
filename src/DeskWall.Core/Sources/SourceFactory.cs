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
            _ => throw new NotSupportedException($"source type '{def.Type}' (source '{def.Name}')"),
        };
    }

    private static int DefaultEvery(string type) => type.ToLowerInvariant() switch
    {
        "disks" => 300,
        "system" => 900,
        _ => 600,
    };
}
