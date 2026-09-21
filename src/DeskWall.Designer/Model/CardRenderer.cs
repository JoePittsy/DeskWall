using DeskWall.Core;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Model;

/// <summary>A gallery card's picture: the widget, rendered by the same Core renderer the wallpaper
/// uses, at 1:1, on a crop of the real base photo. Not an icon and not a mock - what the card shows
/// is what the column will show, which is the only reason the gallery can be picked from at a
/// glance.
/// <para>Cheap enough to do all of them at startup: eight widgets at roughly 306x100 is less than a
/// thousandth of one wallpaper frame.</para></summary>
public sealed record Card(int Width, int Height, byte[] Bgra);

public static class CardRenderer
{
    /// <summary>The card's picture is wider than the 172 px column so the widget sits in a slice of
    /// photo rather than flush against the card's edge.</summary>
    public const int CardWidth = 306, VerticalPadding = 14;

    /// <summary>Render one card. Never throws: a template whose image is missing or whose binding
    /// is wrong comes back as the photo crop with whatever did resolve on it, because a gallery that
    /// disappears is worse than a card that is half right.</summary>
    public static Card Render(WidgetTemplate template, string baseImage, RecordValue values)
    {
        var w = CardWidth;
        var h = template.Height + VerticalPadding * 2;
        var layout = template.Preview(baseImage);
        layout.BaseFit = Fit.Cover;
        var dx = (w - template.Width) / 2;
        foreach (var c in layout.Components) c.Rect = c.Rect.Offset(dx, VerticalPadding);

        IReadOnlyList<Resolved> resolved;
        try { resolved = LayoutResolver.Resolve(layout, values); }
        catch (Exception) { resolved = Array.Empty<Resolved>(); }

        string? baseRaw = null;
        try { baseRaw = BaseCache.Ensure(layout.BaseImage, w, h, Fit.Cover); }
        catch (Exception) { /* no photo: the flat card below still shows the widget */ }

        Surface? surface = null;
        try
        {
            if (baseRaw is not null)
            {
                try { surface = new FrameRenderer(w, h).RenderAll(baseRaw, resolved); }
                catch (Exception) { surface = null; }
            }
            surface ??= Flat(w, h, resolved);
            var bgra = new byte[w * 4 * h];
            surface.CopyTo(bgra);
            return new Card(w, h, bgra);
        }
        finally { surface?.Dispose(); }
    }

    private static Surface Flat(int w, int h, IReadOnlyList<Resolved> resolved)
    {
        var s = Surface.Create(w, h);
        try
        {
            s.Clear(new Color(255, 48, 48, 48));
            foreach (var c in resolved.OrderBy(c => c.Z))
                try { FrameRenderer.Draw(s, c); } catch (Exception) { /* one bad component, not the card */ }
            return s;
        }
        catch { s.Dispose(); throw; }
    }
}
