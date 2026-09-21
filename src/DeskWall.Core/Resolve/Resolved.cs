using DeskWall.Core.Render;

namespace DeskWall.Core.Resolve;

/// <summary>A component after bindings are resolved and repeaters expanded. Renderers and
/// the shortcut manager consume only these. ContentKey is set by the resolver (Task 6).</summary>
public abstract record Resolved(string Id, Rect Rect, int Z)
{
    public string ContentKey { get; init; } = "";

    /// <summary>
    /// Every pixel this component may touch. Identical to <see cref="Rect"/> for everything that
    /// paints inside its box; text inflates it by the effect margin. The incremental renderer
    /// restores the base over this, not over Rect, so nothing survives a redraw. The content key
    /// deliberately stays on Rect: PaintBounds is derived from it and adds no information.
    /// </summary>
    public virtual Rect PaintBounds => Rect;

    /// <summary>The fields that define appearance, in a fixed order, for hashing.</summary>
    public abstract IEnumerable<string> KeyParts();
}

public sealed record ResolvedText(string Id, Rect Rect, int Z, string Text, TextStyle Style) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Text, Style.ToString()];

    /// <summary>Rect plus the margin <see cref="Surface.DrawText"/> clips to. One shared helper
    /// (<see cref="TextStyle.PaintMargin"/>) so the clip and the dirty rect cannot drift apart.</summary>
    public override Rect PaintBounds
    {
        get
        {
            var m = Style.PaintMargin();
            return new Rect(Rect.X - m, Rect.Y - m, Rect.W + 2 * m, Rect.H + 2 * m);
        }
    }
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
