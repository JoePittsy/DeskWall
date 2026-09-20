using DeskWall.Core.Render;

namespace DeskWall.Core.Resolve;

/// <summary>A component after bindings are resolved and repeaters expanded. Renderers and
/// the shortcut manager consume only these. ContentKey is set by the resolver (Task 6).</summary>
public abstract record Resolved(string Id, Rect Rect, int Z)
{
    public string ContentKey { get; init; } = "";

    /// <summary>The fields that define appearance, in a fixed order, for hashing.</summary>
    public abstract IEnumerable<string> KeyParts();
}

public sealed record ResolvedText(string Id, Rect Rect, int Z, string Text, TextStyle Style) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Text, Style.ToString()];
}

public sealed record ResolvedImage(string Id, Rect Rect, int Z, string Path, Fit Fit, float Radius, float Opacity) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Path, Fit.ToString(), Radius.ToString("R"), Opacity.ToString("R")];
}

public sealed record ResolvedBar(string Id, Rect Rect, int Z, double Fraction, Color Track, Color Fill, Axis Direction) : Resolved(Id, Rect, Z)
{
    // Fraction is keyed at 0.1 percent: a 172 px bar cannot show finer, and disk free space wobbles below that between reads.
    public override IEnumerable<string> KeyParts() => [Math.Round(Fraction, 3).ToString("R"), Track.ToHex(), Fill.ToHex(), Direction.ToString()];
}

/// <summary>Draws nothing. The shortcut manager (Phase 3) owns a desktop icon over Rect.</summary>
public sealed record ResolvedShortcut(string Id, Rect Rect, int Z, string Target, string Tooltip, int Slot) : Resolved(Id, Rect, Z)
{
    public override IEnumerable<string> KeyParts() => [Target, Tooltip, Slot.ToString()];
}
