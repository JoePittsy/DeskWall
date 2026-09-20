namespace DeskWall.Core.Render;

public enum TextEffect { None, Shadow, Outline, Plate }

/// <summary>Resolved text appearance. Defaults match the spec: soft shadow, 6 px blur.</summary>
public sealed record TextStyle(
    string Font = "Segoe UI",
    float Size = 16f,
    int Weight = 400,
    Color Color = default,
    Align Align = Align.Left,
    TextEffect Effect = TextEffect.Shadow,
    float EffectRadius = 6f,
    Color EffectColor = default)
{
    public static TextStyle Default => new(Color: Color.White, EffectColor: new Color(160, 0, 0, 0));

    /// <summary>
    /// Pixels this style may paint outside the component rect. The single source of truth for the
    /// clip <see cref="Surface.DrawText"/> pushes and for <c>ResolvedText.PaintBounds</c>: the two
    /// must agree exactly, or the incremental renderer restores the base over a smaller area than
    /// the text painted and leaves permanent residue on the wallpaper.
    /// Shadow/Outline ring outwards by the effect radius (plus a pixel of antialiasing and one for
    /// the (1,1) shadow offset); Plate adds its own 8 px horizontal inset on top of the corner radius.
    /// </summary>
    public int PaintMargin() => Effect switch
    {
        TextEffect.None => 0,
        TextEffect.Plate => 8 + (int)Math.Ceiling(EffectRadius),
        _ => (int)Math.Ceiling(EffectRadius) + 2,
    };
}
