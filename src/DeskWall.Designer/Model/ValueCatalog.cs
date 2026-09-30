using System.Globalization;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Designer.Model;

/// <summary>What a live value is, for the Data panel, the drop plan and the binding chip's filter.
/// <see cref="Fraction"/> is a number documented as 0..1 (docs/sources.md), never guessed from a
/// sample that happens to fall in range.</summary>
public enum ValueKind { Fraction, Timestamp, Number, Text, Bool, List }

/// <param name="Path">Binding path, as a binding writes it: <c>disks.drives[C].usedFraction</c>.</param>
/// <param name="Label">Human label ("CPU load"), or the path when the table has none.</param>
/// <param name="Sample">The value as it is now.</param>
/// <param name="Source">The layout source the value comes from (what a drop adds when it is
/// missing), or null for a pushed provider, which has nothing to add.</param>
/// <param name="Diagnostic">A value about the source rather than about the machine (raw bytes, raw
/// seconds, sample counts, window lengths): the Data list shows it only when searched for.</param>
public sealed record ValueEntry(string Path, string Label, ValueKind Kind, Value Sample, SourceDef? Source, bool Diagnostic = false);

/// <summary>Flattens <see cref="LiveSources.Tree"/> into one row per bindable value.</summary>
public static partial class ValueCatalog
{
    /// <summary>Every leaf of <paramref name="tree"/>, plus each list itself (for a repeater's
    /// items). Records are walked, not listed. <paramref name="sources"/> names each root's source
    /// type, which is what the label table is keyed on; a root with no def (a pushed provider) is
    /// looked up as if its name were its type.</summary>
    public static IReadOnlyList<ValueEntry> From(RecordValue tree, IEnumerable<SourceDef> sources)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(sources);
        var defs = new Dictionary<string, SourceDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources) defs.TryAdd(s.Name, s);
        var result = new List<ValueEntry>();
        foreach (var (name, value) in tree.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var def = defs.GetValueOrDefault(name);
            Walk(result, def, (def?.Type ?? name).ToLowerInvariant(), name, "", null, value);
        }
        return result;
    }

    /// <param name="shape">The path under the source with list keys as <c>[*]</c>: the table key.</param>
    private static void Walk(List<ValueEntry> into, SourceDef? def, string type, string path, string shape, string? key, Value value)
    {
        switch (value)
        {
            case RecordValue r:
                foreach (var (name, v) in r.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                    Walk(into, def, type, path + "." + name, shape.Length == 0 ? name : shape + "." + name, key, v);
                return;
            case ListValue l:
                into.Add(Entry(def, type, path, shape, key, ValueKind.List, l));
                for (var i = 0; i < l.Items.Count; i++)
                {
                    var k = l.KeyField is not null && l.Items[i].Get(l.KeyField) is { } kv ? kv.ToText(null) : i.ToString(CultureInfo.InvariantCulture);
                    Walk(into, def, type, $"{path}[{k}]", shape + "[*]", k, l.Items[i]);
                }
                return;
            default:
                var known = Labels.GetValueOrDefault(type + "." + shape);
                var kind = value switch
                {
                    NumberValue => known.Fraction ? ValueKind.Fraction : ValueKind.Number,
                    TimeValue => ValueKind.Timestamp,
                    BoolValue => ValueKind.Bool,
                    _ => ValueKind.Text,   // text, and an image path
                };
                into.Add(Entry(def, type, path, shape, key, kind, value));
                return;
        }
    }

    private static ValueEntry Entry(SourceDef? def, string type, string path, string shape, string? key, ValueKind kind, Value value)
        => new(path, Labels.TryGetValue(type + "." + shape, out var l) ? string.Format(CultureInfo.InvariantCulture, l.Label, key) : path, kind, value, def,
            Diagnostics.Contains(type + "." + shape));

    /// <summary>Values that answer "is the source working" or repeat another value in raw units;
    /// every one has a readable twin (free GB beside free bytes, Uptime beside uptime seconds).</summary>
    private static readonly HashSet<string> Diagnostics = new(StringComparer.OrdinalIgnoreCase)
    {
        "disks.drives[*].free", "disks.drives[*].total", "disks.drives[*].letter",
        "system.uptime",
        "hardware.samples", "hardware.window",
        "command.exitCode", "command.stderr", "command.running", "command.starts", "command.badLines",
        "http.status", "http.fromCache",
        "file.size",
    };

    /// <summary>What the label table knows about a binding path with no live value to walk (not read
    /// yet, or a source that is not running): its human label ("CPU load", "C: used") and whether it
    /// is documented as 0..1. The path itself, and false, when the table has nothing. The first
    /// segment names the source; <paramref name="sources"/> gives its type, else the name is taken
    /// as the type (every default source's name is its type).</summary>
    public static (string Label, bool Fraction) Known(string path, IEnumerable<SourceDef> sources)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sources);
        var i = path.IndexOfAny(['.', '[']);
        if (i <= 0) return (path, false);
        var name = path[..i];
        var type = (sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Type ?? name).ToLowerInvariant();
        string? key = null;
        var shape = ListKey().Replace(path[i..].TrimStart('.'), m => { key ??= m.Groups[1].Value; return "[*]"; });
        return Labels.TryGetValue(type + "." + shape, out var l) ? (string.Format(CultureInfo.InvariantCulture, l.Label, key), l.Fraction) : (path, false);
    }

    /// <summary>The path half of a binding as a binding writes it, without the format.</summary>
    public static string PathText(DeskWall.Core.Bindings.Binding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < binding.Path.Count; i++)
        {
            if (i > 0 && binding.Path[i] is DeskWall.Core.Bindings.NameSegment) sb.Append('.');
            sb.Append(binding.Path[i]);
        }
        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial System.Text.RegularExpressions.Regex ListKey();

    /// <summary>Whether a value of <paramref name="kind"/> can drive a property edited with
    /// <paramref name="editor"/>. Text-shaped editors take anything that renders as text (a bool
    /// through a map format, "?true=#D13438,false=#EBFFFFFF", which is how colours react); numeric
    /// ones take numbers; a repeater's items take a list.</summary>
    public static bool Fits(PropertySchema.Editor editor, ValueKind kind) => editor switch
    {
        PropertySchema.Editor.Number or PropertySchema.Editor.AutoNumber => kind is ValueKind.Fraction or ValueKind.Number,
        PropertySchema.Editor.Binding => kind is ValueKind.List,
        _ => kind is not ValueKind.List,
    };

    /// <summary>Source type + path under it (lists as <c>[*]</c>) to label, and whether the field is
    /// documented as 0..1. From docs/sources.md; <c>{0}</c> is the list item's key.</summary>
    private static readonly Dictionary<string, (string Label, bool Fraction)> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["time.now"] = ("Time", false),
        ["time.date"] = ("Date", false),
        ["time.weekday"] = ("Weekday", false),
        ["time.dayFraction"] = ("Day progress", true),
        ["time.weekFraction"] = ("Week progress", true),
        ["time.yearFraction"] = ("Year progress", true),
        ["time.dayPercent"] = ("Day progress %", false),
        ["time.weekPercent"] = ("Week progress %", false),
        ["time.yearPercent"] = ("Year progress %", false),
        ["time.phase"] = ("Phase of day", false),

        ["disks.drives"] = ("Drives", false),
        ["disks.drives[*].letter"] = ("{0}: drive letter", false),
        ["disks.drives[*].label"] = ("{0}: drive name", false),
        ["disks.drives[*].free"] = ("{0}: free (bytes)", false),
        ["disks.drives[*].total"] = ("{0}: size (bytes)", false),
        ["disks.drives[*].freeGB"] = ("{0}: free GB", false),
        ["disks.drives[*].totalGB"] = ("{0}: size GB", false),
        ["disks.drives[*].usedFraction"] = ("{0}: used", true),

        ["system.uptime"] = ("Uptime (seconds)", false),
        ["system.uptimeText"] = ("Uptime", false),
        ["system.bootedAt"] = ("Booted at", false),
        ["system.machine"] = ("Machine name", false),
        ["system.user"] = ("User name", false),
        ["system.pendingReboot"] = ("Reboot pending", false),
        ["system.daysSinceCrash"] = ("Days since crash", false),
        ["system.lastCrashAt"] = ("Last crash", false),

        ["hardware.cpu"] = ("CPU load", true),
        ["hardware.cpuPct"] = ("CPU load %", false),
        ["hardware.cpuNow"] = ("CPU load now", true),
        ["hardware.ram"] = ("RAM used", true),
        ["hardware.ramPct"] = ("RAM used %", false),
        ["hardware.ramUsedGB"] = ("RAM used GB", false),
        ["hardware.ramTotalGB"] = ("RAM total GB", false),
        ["hardware.gpu"] = ("GPU load", true),
        ["hardware.gpuPct"] = ("GPU load %", false),
        ["hardware.gpuNow"] = ("GPU load now", true),
        ["hardware.gpuMemory"] = ("GPU memory used", true),
        ["hardware.gpuTempC"] = ("GPU temperature", false),
        ["hardware.gpuTempFraction"] = ("GPU temperature (of 100 C)", true),
        ["hardware.samples"] = ("Hardware samples", false),
        ["hardware.window"] = ("Hardware window (seconds)", false),

        ["battery.percent"] = ("Battery %", false),
["battery.fraction"] = ("Battery charge", true),
["battery.charging"] = ("Charging", false),
["battery.onBattery"] = ("On battery", false),
["battery.minutesLeft"] = ("Battery minutes left", false),
["battery.opacity"] = ("Battery visibility", false),
        ["hardware.cpuHistory"] = ("CPU history", false),
        ["hardware.ramHistory"] = ("RAM history", false),
        ["hardware.gpuHistory"] = ("GPU history", false),
        ["disks.worstUsedFraction"] = ("Fullest drive", true),
        ["media.title"] = ("Now playing", false),
        ["media.artist"] = ("Artist", false),
        ["media.album"] = ("Album", false),
        ["media.playing"] = ("Playing", false),
        ["media.art"] = ("Album art", false),
        ["media.app"] = ("Media app", false),
        ["audio.volume"] = ("Volume", true),
        ["audio.volumePct"] = ("Volume %", false),
        ["audio.muted"] = ("Muted", false),
        ["audio.device"] = ("Audio device", false),

        ["notifications.status"] = ("Notification access", false),
        ["notifications.count"] = ("Notifications", false),
        ["notifications.apps"] = ("Notifying apps", false),
        ["notifications.apps[*].name"] = ("{0}: app", false),
        ["notifications.apps[*].count"] = ("{0}: notifications", false),
        ["notifications.latestApp"] = ("Latest notification app", false),
        ["notifications.latestTitle"] = ("Latest notification", false),
        ["notifications.latestText"] = ("Latest notification text", false),
        ["notifications.latestAt"] = ("Latest notification at", false),
        ["notifications.changedAt"] = ("Notifications changed at", false),

        ["command.text"] = ("Command output", false),
        ["command.exitCode"] = ("Exit code", false),
        ["command.ranAt"] = ("Ran at", false),
        ["command.stderr"] = ("Command errors", false),
        ["command.running"] = ("Running", false),
        ["command.starts"] = ("Starts", false),
        ["command.badLines"] = ("Bad lines", false),

        ["http.text"] = ("Response", false),
        ["http.status"] = ("HTTP status", false),
        ["http.fetchedAt"] = ("Fetched at", false),
        ["http.fromCache"] = ("From cache", false),

        ["rss.title"] = ("Feed title", false),
        ["rss.link"] = ("Feed link", false),
        ["rss.items"] = ("Feed items", false),
        ["rss.items[*].title"] = ("Headline", false),
        ["rss.items[*].link"] = ("Headline link", false),
        ["rss.items[*].published"] = ("Published", false),
        ["rss.items[*].summary"] = ("Summary", false),
        ["rss.items[*].author"] = ("Author", false),

        ["file.text"] = ("File text", false),
        ["file.modifiedAt"] = ("File modified", false),
        ["file.size"] = ("File size (bytes)", false),
        ["file.exists"] = ("File exists", false),
        ["file.title"] = ("Feed title", false),
        ["file.items"] = ("Feed items", false),
        ["file.items[*].title"] = ("Headline", false),
        ["file.items[*].published"] = ("Published", false),
    };
}
