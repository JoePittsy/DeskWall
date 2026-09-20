using DeskWall.Core.Layout;

namespace DeskWall.Core.Sources;

public static class SourceFactory
{
    public static ISource Create(SourceDef def, IClock clock)
    {
        var every = TimeSpan.FromSeconds(def.EverySeconds ?? DefaultEvery(def.Type));
        return def.Type.ToLowerInvariant() switch
        {
            "time" => new TimeSource(def.Name, clock),
            "disks" => new DisksSource(def.Name, every),
            // Phase 4 adds: http, rss, file, command, system
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
