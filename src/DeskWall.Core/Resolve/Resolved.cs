using DeskWall.Core.Render;

namespace DeskWall.Core.Resolve;

/// <summary>A component after bindings are resolved and repeaters expanded. Renderers and
/// the shortcut manager consume only these. ContentKey is set by the resolver (Task 6).</summary>
public abstract record Resolved(string Id, Rect Rect, int Z)
{
    public string ContentKey { get; init; } = "";

    /// <summary>
    /// Every pixel this component may touch. Identical to <see cref="Rect"/> for everything that
    /// paints inside its box; text is measured, because it does not. The incremental renderer
    /// restores the base over this, not over Rect, so nothing survives a redraw.
    /// <para>
    /// The content key deliberately stays on <see cref="Rect"/>. For everything but text PaintBounds
    /// is derived from it and adds no information; for text it is a pure function of the text, the
    /// style and the rect, all of which the key already covers, so keying on it as well would only
    /// make the key more expensive to compute. <c>TickRunner</c> instead compares the measured
    /// bounds against the ones it persisted last tick, which catches the one case the key cannot:
    /// the same string in the same style measuring differently because the font behind it changed.
    /// </para>
    /// </summary>
    public virtual Rect PaintBounds => Rect;

    /// <summary>The fields that define appearance, in a fixed order, for hashing.</summary>
    public abstract IEnumerable<string> KeyParts();
}

public sealed record ResolvedText(string Id, Rect Rect, int Z, string Text, TextStyle Style) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Text, Style.ToString()];

    /// <summary>
    /// The measured glyph ink plus the effect margin -- what the text actually covers, not the box
    /// it was authored in. <see cref="Surface.DrawText"/> clips to the same call, so the clip and
    /// the dirty rect are one value rather than two that have to be kept in step.
    /// <para>
    /// Computed on demand off <see cref="TextMeasure"/>'s cache rather than carried as a field: a
    /// <c>ResolvedText</c> built anywhere -- the resolver, the designer, a test -- then has correct
    /// bounds with nothing to remember, and a <c>with</c> expression cannot leave a stale
    /// measurement behind. The cost is a dictionary lookup.
    /// </para>
    /// <para>
    /// Unlike every other component's, this can <em>shrink</em> between ticks: "100%" becoming "9%"
    /// returns a narrower rect. Anything comparing it across ticks must dirty the union of the old
    /// and the new, or the base is never restored over what the longer string painted.
    /// </para>
    /// </summary>
    public override Rect PaintBounds => TextMeasure.PaintBounds(Text, Style, Rect);
}

public sealed record ResolvedImage(string Id, Rect Rect, int Z, string Path, Fit Fit, float Radius, float Opacity) : Resolved(Id, Rect, Z)
{
    // Finding 14: without the file's own mtime, a revalidated cache file (Phase 4's remote image
    // cache keeps the same path across a content refresh) or any cover replaced in place never
    // redraws, because path/fit/radius/opacity are unchanged.
    public override IEnumerable<string> KeyParts() => [Path, Fit.ToString(), Radius.ToString("R"), Opacity.ToString("R"), MTimeKeyPart()];

    private string MTimeKeyPart() => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path).Ticks.ToString() : "missing";
}

public sealed record ResolvedBar(string Id, Rect Rect, int Z, double Fraction, Color Track, Color Fill, Axis Direction) : Resolved(Id, Rect, Z)
{
    // Fraction is keyed at 0.1 percent: a 172 px bar cannot show finer, and disk free space wobbles below that between reads.
    public override IEnumerable<string> KeyParts() => [Math.Round(Fraction, 3).ToString("R"), Track.ToHex(), Fill.ToHex(), Direction.ToString()];
}

/// <summary>A thin arc: Track over the full Sweep, Fill over Sweep * Fraction, both Thickness px
/// wide and centred in Rect. Nothing else is drawn; a number inside is a text component.</summary>
public sealed record ResolvedDial(string Id, Rect Rect, int Z, double Fraction, Color Track, Color Fill, float Thickness, float StartAngle, float Sweep) : Resolved(Id, Rect, Z)
{
    // Same quantisation as ResolvedBar: a 5-minute average moves below a pixel between reads.
    public override IEnumerable<string> KeyParts() =>
        [Math.Round(Fraction, 3).ToString("R"), Track.ToHex(), Fill.ToHex(), Thickness.ToString("R"), StartAngle.ToString("R"), Sweep.ToString("R")];
}

/// <summary>Draws nothing. The shortcut manager (Phase 3) owns a desktop icon over Rect.</summary>
public sealed record ResolvedShortcut(string Id, Rect Rect, int Z, string Target, string Tooltip, int Slot) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Target, Tooltip, Slot.ToString()];
}
