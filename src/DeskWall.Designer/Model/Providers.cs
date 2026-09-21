using System.IO;
using DeskWall.Core.Events;
using DeskWall.Core.Values;

namespace DeskWall.Designer.Model;

/// <summary>One bindable field of a provider, as the panel and the binding picker show it.</summary>
/// <param name="Path">The path inside the provider, so the binding is "&lt;provider&gt;.&lt;Path&gt;".</param>
/// <param name="Value">What was last observed, or null for a field only a manifest knows about
/// and for one that is computed at each refresh (ageSeconds).</param>
public sealed record ProviderFieldView(string Path, string Type, string? Value, string? Example, string? Description);

/// <summary>A provider as the designer can describe it: what a manifest says and what was last
/// seen, together. Spec section 5: where the two disagree, the observed value wins for rendering
/// and the manifest wins for describing.</summary>
public sealed record ProviderView(
    string Name,
    string Description,
    bool Remembered,
    bool Described,
    string? ManifestPath,
    DateTimeOffset? ReceivedAt,
    int? ExpectEverySeconds,
    IReadOnlyList<ProviderFieldView> Fields);

/// <summary>What the providers panel reads and does. The daemon owns the pipe and writes
/// events.json; this reads that file and the manifest directories, and its two verbs are the ones
/// spec section 8 asks for - Forget a remembered record, and Describe a provider that has none.
/// <para>The four seams are delegates rather than paths so a test can drive it without a runtime
/// directory; <see cref="Default"/> wires the real ones.</para></summary>
public sealed class ProvidersModel(
    Func<IReadOnlyList<ProviderRecord>> readRecords,
    Action<IEnumerable<ProviderRecord>> writeRecords,
    Func<IReadOnlyList<ProviderManifest>> readManifests,
    string manifestDir)
{
    private List<ProviderRecord> _records = [];
    private List<ProviderManifest> _manifests = [];

    public static ProvidersModel Default() => new(
        EventStore.Load,
        EventStore.Save,
        () => ProviderCatalog.Load(ProviderCatalog.ShippedDir, ProviderCatalog.UserDir),
        ProviderCatalog.UserDir);

    /// <summary>Why the last load could not read something, or null. A manifest a user is midway
    /// through editing must not take the panel down.</summary>
    public string? LastError { get; private set; }

    /// <summary>The records as last read, for merging into the designer's own value tree.</summary>
    public IReadOnlyList<ProviderRecord> Records => _records;

    /// <summary>Re-read both sources and merge them. Cheap enough to call on every file change.</summary>
    public IReadOnlyList<ProviderView> Load()
    {
        LastError = null;
        try { _records = [.. readRecords()]; }
        catch (Exception ex) { _records = []; LastError = ex.Message; }
        try { _manifests = [.. readManifests()]; }
        catch (Exception ex) { _manifests = []; LastError = ex.Message; }

        var byName = new Dictionary<string, ProviderView>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var r in _records)
        {
            if (!byName.ContainsKey(r.Name)) order.Add(r.Name);
            byName[r.Name] = Merge(r, Find(r.Name));
        }
        foreach (var m in _manifests)
        {
            if (byName.ContainsKey(m.Name)) continue;   // already merged from its record
            order.Add(m.Name);
            byName[m.Name] = Merge(null, m);
        }
        return order.Select(n => byName[n]).ToList();
    }

    /// <summary>Drop a remembered record. Silent about a name it does not have: the daemon may
    /// have saved over the file between the panel's last read and this click.</summary>
    public void Forget(string name)
    {
        var kept = _records.Where(r => !string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == _records.Count) return;
        writeRecords(kept);
        _records = kept;
    }

    /// <summary>Write providers/&lt;name&gt;.json seeded from what has been observed, and return
    /// the path. Every field gets a description placeholder, because the file exists precisely so
    /// the author can write the descriptions that observation cannot supply.</summary>
    public string Describe(string name)
    {
        var view = Load().FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no provider named '{name}'", nameof(name));
        Directory.CreateDirectory(manifestDir);
        var path = Path.Combine(manifestDir, view.Name + ".json");
        var manifest = new ProviderManifest
        {
            Name = view.Name,
            Description = view.Described ? view.Description : $"What {view.Name} publishes. Describe it here.",
            ExpectEverySeconds = view.ExpectEverySeconds,
            Fields = view.Fields
                .Select(f => new ProviderField(f.Path, f.Type, f.Example ?? f.Value, f.Description ?? "What this is, in a few words."))
                .ToList(),
        };
        File.WriteAllText(path, ProviderManifest.ToJson(manifest));
        return path;
    }

    private ProviderManifest? Find(string name)
        => _manifests.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ProviderView Merge(ProviderRecord? record, ProviderManifest? manifest)
    {
        var name = record?.Name ?? manifest!.Name;
        var observed = record is null ? [] : Observed(record);
        var order = new List<string>();
        var fields = new Dictionary<string, ProviderFieldView>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in observed)
        {
            order.Add(f.Path);
            fields[f.Path] = f;
        }
        // A manifest field the producer has not sent yet is still bindable, which is the point of
        // having one: a widget can be built against an app the user has not started.
        foreach (var f in manifest?.Fields ?? [])
        {
            if (fields.TryGetValue(f.Path, out var seen))
                fields[f.Path] = seen with { Example = f.Example, Description = f.Description };
            else
            {
                order.Add(f.Path);
                fields[f.Path] = new ProviderFieldView(f.Path, f.Type, null, f.Example, f.Description);
            }
        }
        return new ProviderView(
            name,
            manifest?.Description ?? (record is null ? "" : "Pushed here; nothing describes it yet."),
            Remembered: record is not null,
            Described: manifest is not null,
            manifest?.Path,
            record?.ReceivedAt,
            manifest?.ExpectEverySeconds,
            order.Select(p => fields[p]).ToList());
    }

    /// <summary>The fields a layout can bind, in the shape ProviderRecord.ToValues publishes them.
    /// ageSeconds is listed with no value: it is recomputed at every refresh, so a value here
    /// would be a number that is already wrong.</summary>
    private static List<ProviderFieldView> Observed(ProviderRecord r)
    {
        var list = new List<ProviderFieldView>();
        Flatten("data", r.Data, list);
        if (r.Type is not null) list.Add(new ProviderFieldView("type", "text", r.Type, null, null));
        if (r.Subject is not null) list.Add(new ProviderFieldView("subject", "text", r.Subject, null, null));
        if (r.Id is not null) list.Add(new ProviderFieldView("id", "text", r.Id, null, null));
        if (r.SentAt is { } sent) list.Add(new ProviderFieldView("sentAt", "time", sent.ToString("u"), null, null));
        list.Add(new ProviderFieldView("receivedAt", "time", r.ReceivedAt.ToString("u"), null, null));
        list.Add(new ProviderFieldView("ageSeconds", "number", null, null, "Seconds since the last event, recomputed every refresh."));
        return list;
    }

    private static void Flatten(string path, Value value, List<ProviderFieldView> into)
    {
        switch (value)
        {
            case RecordValue r:
                foreach (var (key, child) in r.Fields) Flatten(path + "." + key, child, into);
                break;
            case TextValue t: into.Add(new ProviderFieldView(path, "text", t.Text, null, null)); break;
            case NumberValue n: into.Add(new ProviderFieldView(path, "number", n.ToText(null), null, null)); break;
            case BoolValue b: into.Add(new ProviderFieldView(path, "bool", b.ToText(null), null, null)); break;
            case TimeValue t: into.Add(new ProviderFieldView(path, "time", t.Time.ToString("u"), null, null)); break;
            case ImageValue i: into.Add(new ProviderFieldView(path, "image", i.Path, null, null)); break;
            case ListValue l: into.Add(new ProviderFieldView(path, "list", $"{l.Items.Count} items", null, null)); break;
        }
    }
}
