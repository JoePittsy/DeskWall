namespace DeskWall.Core.Render;

public enum TextEffect { None, Shadow, Outline, Plate }

/// <summary>Resolved text appearance. Defaults match the spec: soft shadow, blur proportional to
/// the font size.</summary>
public sealed record TextStyle(
    string Font = "Segoe UI",
    float Size = 16f,
    int Weight = 400,
    Color Color = default,
    Align Align = Align.Left,
    TextEffect Effect = TextEffect.Shadow,
    float? EffectRadius = null,
    Color EffectColor = default)
{
    public static TextStyle Default => new(Color: Color.White, EffectColor: new Color(160, 0, 0, 0));

    /// <summary>
    /// The blur radius actually used: <see cref="EffectRadius"/> when the layout gave one, otherwise
    /// <see cref="DefaultRadius"/> of this style's <see cref="Size"/>. Every renderer and
    /// <see cref="PaintMargin"/> reads this, never the nullable field, so an unset radius behaves
    /// the same everywhere.
    /// </summary>
    public float Radius => EffectRadius ?? DefaultRadius(Size);

    /// <summary>
    /// A tenth of the font size, never less than one pixel: 64 -&gt; 6, 40 -&gt; 4, 13 -&gt; 1.
    /// <para>
    /// The radius used to be a flat 6 px whatever the size. On the owner's 64 px clock that is the
    /// tasteful drop shadow it was tuned as; on the 13 px labels in the same layout it is about half
    /// the cap height, so the stacked shadow copies close the counters inside the glyphs and the
    /// text reads as a dark crust rather than a shadow -- which over a bright photo looks like a
    /// low-resolution image. Re-rendering the owner's live layout at a tenth of the size per label
    /// left the clock pixel-identical and made the small labels crisp, which is what fixed the
    /// value here. The floor of 1 matters because the shadow path already clamps to 1 internally
    /// (<see cref="Surface.DrawText"/>), so anything smaller would only disagree with what is drawn.
    /// </para>
    /// </summary>
    public static float DefaultRadius(float size) => Math.Max(1f, MathF.Round(size * 0.10f));

    /// <summary>
    /// Pixels this style may paint outside the <em>glyph ink</em>, which is what the effects paint
    /// and the glyphs do not. <see cref="TextMeasure.PaintBounds"/> adds it to the measured ink to
    /// get the painted area; it no longer stands in for the extent of the text itself, which is
    /// measured (it used to be the whole allowance on top of the authored rect, which is why
    /// oversized text was clipped away).
    /// Shadow/Outline ring outwards by the effect radius (plus a pixel of antialiasing and one for
    /// the (1,1) shadow offset); Plate adds its own 8 px horizontal inset on top of the corner radius.
    /// <para>Derived from <see cref="Radius"/>, so small text now gets a proportionally small margin
    /// and stops reserving -- and repainting -- a ring it never uses.</para>
    /// </summary>
    public int PaintMargin() => Effect switch
    {
        TextEffect.None => 0,
        TextEffect.Plate => 8 + (int)Math.Ceiling(Radius),
        _ => (int)Math.Ceiling(Radius) + 2,
    };
}
