using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>A gallery card's picture: the widget, rendered by the same Core renderer the wallpaper
/// uses, at 1:1, on flat neutral grey. Not an icon and not a mock - what the card shows is what the
/// column will show, which is the only reason the gallery can be picked from at a glance.
/// <para>Owner, 2026-09-21: the cards used to render on a crop of the real base photo. A picture of
/// a forest behind a 40 px readout at card size is noise - the leaves and the widget are the same
/// few hundred pixels - and the same crop behind all eight made them hard to tell apart. The photo
/// is still exactly as it will be on the centre preview, which is where that question is asked.</para>
/// <para>Cheap enough to do all of them at startup: eight widgets at roughly 306x100 is less than a
/// thousandth of one wallpaper frame, and now without a photo decode each.</para></summary>
public sealed record Card(int Width, int Height, byte[] Bgra);

public static class CardRenderer
{
    /// <summary>The card's picture is wider than the 172 px column so the widget sits in a field of
    /// its own rather than flush against the card's edge.</summary>
    public const int CardWidth = 306, VerticalPadding = 14;

    /// <summary>The card background. Neutral, dark and the same in both themes: a widget's text,
    /// arcs and bars are white or near-white because they were drawn to sit on a photograph, and on
    /// a light card half of every widget would vanish.</summary>
    public static readonly Color Background = new(255, 0x2B, 0x2B, 0x2B);

    /// <summary>Render one card. Never throws: a template whose image is missing or whose binding is
    /// wrong comes back as the grey field with whatever did resolve on it, because a gallery that
    /// disappears is worse than a card that is half right.</summary>
    public static Card Render(WidgetTemplate template, RecordValue values)
    {
        var w = CardWidth;
        var h = template.Height + VerticalPadding * 2;
        var layout = template.Preview("");
        var dx = (w - template.Width) / 2;
        foreach (var c in layout.Components) c.Rect = c.Rect.Offset(dx, VerticalPadding);

        IReadOnlyList<Resolved> resolved;
        try { resolved = LayoutResolver.Resolve(layout, values); }
        catch (Exception) { resolved = Array.Empty<Resolved>(); }

        using var surface = Surface.Create(w, h);
        surface.Clear(Background);
        foreach (var c in resolved.OrderBy(c => c.Z))
            try { FrameRenderer.Draw(surface, c); } catch (Exception) { /* one bad component, not the card */ }

        var bgra = new byte[w * 4 * h];
        surface.CopyTo(bgra);
        return new Card(w, h, bgra);
    }
}
