using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Settings: path (required; %ENV% expanded); parse = json | text | rss (default by extension: .json,
/// .xml/.rss/.atom => rss, else text); every (default 30 s: the cheap re-check of mtime; the daemon also
/// wakes on the file watcher in Phase 2 style, Task 8); unixTimeFields as for http. Publishes: json | text |
/// (for rss) title/link/items, plus modifiedAt (TimeValue), size (NumberValue), exists (BoolValue, always true).
/// NextDue returns now when the file's mtime changed since the last refresh, so the tick picks a change up on any wake.
/// A missing file throws (spec 3.2): the previous values stay published rather than the column blanking.</summary>
#pragma warning disable CS9113 // clock is kept for parity with SourceFactory.Create(def, clock) / FromDef; this source derives due-ness from file mtime, not the clock, until Phase 2 Task 8 wires the file watcher.
public sealed class FileSource(string name, TimeSpan every, string path, string? parse, IReadOnlySet<string> unixTimeFields, IClock clock) : ISource
#pragma warning restore CS9113
{
    private DateTime _seenMtime;

    public string Name => name;
    public string Path { get; } = Environment.ExpandEnvironmentVariables(path);

    public static FileSource FromDef(SourceDef def, IClock clock)
    {
        var s = def.Settings;
        if (!s.TryGetValue("path", out var p) || string.IsNullOrWhiteSpace(p)) throw new ArgumentException($"file source '{def.Name}' needs settings.path");
        var unix = new HashSet<string>((s.TryGetValue("unixTimeFields", out var u) ? u : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        return new FileSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 30), p, s.TryGetValue("parse", out var mode) ? mode : null, unix, clock);
    }

    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        var mtime = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
        return mtime != _seenMtime ? now : lastRefresh.Value + every;
    }

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Path))
        {
            // Spec 3.2: a source that cannot produce its values throws, so SourceSnapshot.Failed keeps
            // the previous ones. Publishing { exists: false } was a *successful* partial record that
            // installed itself over the last good values, so a producer writing its JSON by
            // delete-then-create blanked the whole column for a tick instead of holding the last list.
            // A layout that wants to say "file missing" does it in the component's fallback.
            _seenMtime = DateTime.MinValue;
            throw new FileNotFoundException($"file source '{name}': no file at {Path}", Path);
        }
        var info = new FileInfo(Path);
        _seenMtime = info.LastWriteTimeUtc;
        var text = File.ReadAllText(Path);
        var mode = parse ?? (info.Extension.ToLowerInvariant() switch { ".json" => "json", ".xml" or ".rss" or ".atom" => "rss", _ => "text" });
        switch (mode)
        {
            case "json": d["json"] = JsonValues.Parse(text, unixTimeFields); break;
            case "rss":
                foreach (var (k, v) in RssSource.ParseFeed(text, 50).Fields) d[k] = v;
                break;
            default: d["text"] = new TextValue(text); break;
        }
        d["exists"] = new BoolValue(true);
        d["modifiedAt"] = new TimeValue(new DateTimeOffset(info.LastWriteTimeUtc).ToLocalTime());
        d["size"] = new NumberValue(info.Length);
        return new(new RecordValue(d));
    }
}
