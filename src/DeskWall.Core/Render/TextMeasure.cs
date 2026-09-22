using System.Collections.Concurrent;
using Windows.Win32.Graphics.DirectWrite;

namespace DeskWall.Core.Render;

/// <summary>
/// The one place that turns (text, style, box) into the pixels a string actually covers, and the
/// only place that builds an <c>IDWriteTextLayout</c> from a <see cref="TextStyle"/>.
/// <para>
/// Both halves matter. <see cref="Surface.DrawText"/> clips to <see cref="PaintBounds"/> and
/// <c>ResolvedText.PaintBounds</c> returns the same value, so the clip and the incremental
/// renderer's dirty rect cannot drift apart. And because the draw and the measurement share
/// <see cref="CreateLayout"/>, the measurement is of the layout that is actually drawn: a second
/// copy of the format setup (alignment, word wrapping, the locale) would measure something subtly
/// different and the clip would start eating glyphs again.
/// </para>
/// <para>
/// Why measure at all: the layout box is <c>rect.W x rect.H</c> with wrapping off, so a string
/// larger than its rect overflows it. Clipping to the authored rect (what this code did before)
/// trimmed the overflow; with <see cref="Align.Right"/> the run starts <em>left</em> of
/// <c>rect.X</c>, so the whole string vanished instead. Deriving the clip from the measurement
/// means oversized text overflows visibly -- the author sees the mistake -- and the renderer still
/// knows every pixel it has to clean up later.
/// </para>
/// </summary>
public static unsafe class TextMeasure
{
    /// <summary>What a measurement depends on. Deliberately not the whole <see cref="TextStyle"/>:
    /// colour and effect do not move a glyph, and keying on them would miss the cache every time a
    /// threshold recoloured a number.</summary>
    private readonly record struct Key(string Text, string Font, float Size, int Weight, Align Align, int W, int H);

    // Resolution runs before the tick's skip gate and TickRunner asks every component for its paint
    // bounds on every tick, so an uncached measurement would be a DirectWrite layout per text
    // component per minute forever. Measurement is a pure function of the key above, so cache it.
    // Bounded by clearing wholesale, as LayoutResolver's image-size cache is: the only thing that
    // grows the set is genuinely new strings, and a layout cycling through a thousand of them
    // between clears is not a layout anyone writes.
    private const int CacheCap = 1024;
    private static readonly ConcurrentDictionary<Key, Rect> s_ink = new();

    /// <summary>
    /// The glyph ink: every pixel the string itself covers, in the same coordinate space as
    /// <paramref name="box"/>, with no allowance for the effect ring. Empty (zero width and height,
    /// at the box origin) for empty text, which paints nothing.
    /// <para>Not clamped to <paramref name="box"/>: overflowing it is exactly what this has to
    /// report. Rounded outwards, so a partially covered pixel counts as covered.</para>
    /// </summary>
    public static Rect Ink(string text, TextStyle style, Rect box)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (string.IsNullOrEmpty(text)) return new Rect(box.X, box.Y, 0, 0);
        var w = Math.Max(0, box.W);
        var h = Math.Max(0, box.H);
        var key = new Key(text, style.Font, style.Size, style.Weight, style.Align, w, h);
        if (!s_ink.TryGetValue(key, out var local))
        {
            local = MeasureInk(text, style, w, h);
            if (s_ink.Count >= CacheCap) s_ink.Clear();
            s_ink[key] = local;
        }
        return local.Offset(box.X, box.Y);
    }

    /// <summary>
    /// Every pixel <see cref="Surface.DrawText"/> may touch: the glyph ink inflated by
    /// <see cref="TextStyle.PaintMargin"/>, which covers what paints outside the glyph box -- the
    /// shadow ring and its (1,1) offset, the outline ring, and the Plate effect's own inset.
    /// <para>
    /// Content-dependent, and so able to <em>shrink</em>: "100%" becoming "9%" returns a smaller
    /// rect than last tick's. Anything that persists these across ticks must dirty the union of the
    /// old and the new (see <see cref="FrameRenderer.RenderIncremental"/>), or the base is never
    /// restored over what the longer string painted and the residue stays on the wallpaper for good.
    /// </para>
    /// </summary>
    public static Rect PaintBounds(string text, TextStyle style, Rect box)
    {
        var ink = Ink(text, style, box);
        if (ink.W <= 0 || ink.H <= 0) return ink;
        var m = style.PaintMargin();
        return new Rect(ink.X - m, ink.Y - m, ink.W + 2 * m, ink.H + 2 * m);
    }

    /// <summary>A DirectWrite layout for this style over a <paramref name="maxW"/> x
    /// <paramref name="maxH"/> box, origin at (0,0). The caller releases it. Word wrapping is off:
    /// the layouts here are single-line readouts, and a rect too small for its string must overflow
    /// rather than silently reflow.</summary>
    internal static IDWriteTextLayout* CreateLayout(string text, TextStyle style, int maxW, int maxH)
    {
        var dw = Surface.DWriteFactory;
        IDWriteTextFormat* fmt;
        fixed (char* fam = style.Font)
        fixed (char* loc = "en-GB")
            dw->CreateTextFormat(fam, null, (DWRITE_FONT_WEIGHT)Math.Clamp(style.Weight, 1, 999), DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, style.Size, loc, &fmt);
        try
        {
            fmt->SetTextAlignment(style.Align switch
            {
                Align.Right => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
                Align.Center => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
                _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
            });
            fmt->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
            fmt->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);

            // A clamped repeater child can have a zero-width or zero-height rect; DirectWrite lays
            // out a box of that size happily (wrapping is off, so the run simply overhangs it) but
            // rejects a negative one.
            IDWriteTextLayout* layout;
            fixed (char* p = text) dw->CreateTextLayout(p, (uint)text.Length, fmt, Math.Max(0, maxW), Math.Max(0, maxH), &layout);
            return layout;
        }
        finally { fmt->Release(); }
    }

    /// <summary>The ink box relative to the layout origin, for a box of <paramref name="w"/> x
    /// <paramref name="h"/>.</summary>
    private static Rect MeasureInk(string text, TextStyle style, int w, int h)
    {
        var layout = CreateLayout(text, style, w, h);
        try
        {
            DWRITE_TEXT_METRICS m; layout->GetMetrics(&m);
            DWRITE_OVERHANG_METRICS o; layout->GetOverhangMetrics(&o);

            // Two boxes, unioned. GetMetrics gives the laid-out text box (m.left is negative when a
            // trailing-aligned run is wider than its layout box -- the case that made right-aligned
            // text disappear). GetOverhangMetrics gives how far the visible ink overshoots each edge
            // of the layout box, positive outwards, so it catches italic overhang, accents and any
            // glyph whose ink exceeds its advance width, and goes negative where the box is larger
            // than the text -- which is how a tall rect round a short string measures small.
            var left = Math.Min(m.left, -o.left);
            var top = Math.Min(m.top, -o.top);
            var right = Math.Max(m.left + m.width, w + o.right);
            var bottom = Math.Max(m.top + m.height, h + o.bottom);
            if (right < left) right = left;
            if (bottom < top) bottom = top;

            var x0 = (int)Math.Floor(left);
            var y0 = (int)Math.Floor(top);
            return new Rect(x0, y0, (int)Math.Ceiling(right) - x0, (int)Math.Ceiling(bottom) - y0);
        }
        finally { layout->Release(); }
    }
}
