using System.IO;
using System.Net.Http;
using System.Text.Json;
using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>Adds, edits and removes linked widget copies on a v2 <see cref="LayoutFile"/>
/// (<c>docs/layout-format.md</c> "Copies", plan D1). A copy is a key, a position and the knobs the
/// owner changed; its parts only exist in the expansion (<see cref="WidgetExpander"/>), so nothing
/// here touches <see cref="LayoutFile.Components"/> or <see cref="LayoutFile.Sources"/>.</summary>
public static class Copies
{
    /// <summary>The size a copy whose widget is missing or broken is drawn and hit-tested at: the
    /// column's width, one line high. Big enough to see and grab, so it is never a silent blank.</summary>
    public static readonly Rect BrokenSize = new(0, 0, 172, 40);

    /// <summary>Places a copy of <paramref name="t"/> with its origin at (x, y). Knobs start at their
    /// defaults, which are not stored. Makes the layout version 2. Returns the copy id,
    /// "&lt;key&gt;-&lt;n&gt;" with n the first free.</summary>
    public static string Add(LayoutFile layout, WidgetTemplate t, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(t);
        layout.Copies ??= [];
        layout.Version = Math.Max(layout.Version, 2);
        var id = FreeId(layout, t.Key);
        layout.Copies.Add(new WidgetCopy { Id = id, Widget = t.Key, X = x, Y = y });
        return id;
    }

    /// <summary>"&lt;key&gt;-&lt;n&gt;" with n the first free: a new copy's id (Add, paste, duplicate).</summary>
    public static string FreeId(LayoutFile layout, string key)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var n = 1;
        while (Taken(layout, $"{key}-{n}")) n++;
        return $"{key}-{n}";
    }

    /// <summary>An id is taken by another copy, or by a loose component that still carries a v1
    /// stamped id ("clock-1.clock", left loose by the migrator): the expansion would otherwise hold
    /// two components with one id.</summary>
    private static bool Taken(LayoutFile layout, string id)
        => layout.Copies?.Any(c => c.Id == id) == true
           || layout.Components.Any(c => c.Id.StartsWith(id + ".", StringComparison.Ordinal));

    /// <summary>False when there is no such copy. Its sources go with it: they only ever existed in
    /// the expansion.</summary>
    public static bool Remove(LayoutFile layout, string copyId)
        => layout.Copies?.RemoveAll(c => c.Id == copyId) > 0;

    public static WidgetCopy? Find(LayoutFile layout, string copyId)
        => layout.Copies?.FirstOrDefault(c => c.Id == copyId);

    /// <summary>Records a knob value on the copy. A value equal to the knob's default removes the
    /// entry instead, so a later change to the default reaches this copy (plan D1).</summary>
    public static void SetKnob(LayoutFile layout, WidgetTemplate t, string copyId, string knobId, string value)
    {
        ArgumentNullException.ThrowIfNull(t);
        var copy = Find(layout, copyId) ?? throw new ArgumentException($"no copy \"{copyId}\"", nameof(copyId));
        var knob = t.Knobs.FirstOrDefault(k => k.Id == knobId)
            ?? throw new ArgumentException($"widget \"{t.Key}\" has no knob \"{knobId}\"", nameof(knobId));
        if (string.Equals(value, knob.Default, StringComparison.Ordinal)) copy.Knobs.Remove(knobId);
        else copy.Knobs[knobId] = value;
    }

    /// <summary>What the knobs panel shows for a knob: the copy's value, else the default.</summary>
    public static string KnobValue(WidgetCopy copy, Knob knob)
        => copy.Knobs.TryGetValue(knob.Id, out var v) ? v : knob.Default;

    /// <summary>The copy's parts in the expansion, in paint order.</summary>
    public static IReadOnlyList<ComponentDef> Components(Expansion expansion, string copyId)
        => expansion.Layout.Components.Where(c => c.Widget == copyId).ToList();

    /// <summary>Where the copy is on the canvas: the union of its expanded parts; else (every part
    /// hidden, or a widget with none) the widget's own size at the copy's origin; else, for a missing
    /// or broken widget, <see cref="BrokenSize"/> there.</summary>
    public static Rect Bounds(WidgetCopy copy, Expansion expansion, Func<string, WidgetTemplate?> find)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var parts = Components(expansion, copy.Id);
        if (parts.Count > 0)
        {
            var minX = parts.Min(c => c.Rect.X);
            var minY = parts.Min(c => c.Rect.Y);
            return new Rect(minX, minY, parts.Max(c => c.Rect.Right) - minX, parts.Max(c => c.Rect.Bottom) - minY);
        }
        var broken = expansion.Problems.Any(p => p.CopyId == copy.Id && p.Kind is ExpandProblemKind.MissingWidget or ExpandProblemKind.BrokenWidget);
        var t = broken ? null : TryFind(find, copy.Widget);
        return t is null
            ? new Rect(copy.X, copy.Y, BrokenSize.W, BrokenSize.H)
            : new Rect(copy.X, copy.Y, t.Width, t.Height);
    }

    /// <summary>A finder that throws for an unloadable file (<see cref="WidgetCatalog.Finder"/>)
    /// answered as "not found": the expansion has already reported it.</summary>
    public static WidgetTemplate? TryFind(Func<string, WidgetTemplate?> find, string key)
    {
        ArgumentNullException.ThrowIfNull(find);
        try { return find(key); }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Open-Meteo geocoding, first match. Null when the town has no match or the request
    /// fails (never throws for a bad town name; a network fault is the caller's problem).</summary>
    public static async Task<(double lat, double lon)?> ResolveTownAsync(string town, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(town)}&count=1";
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;
        var first = results[0];
        return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }
}
