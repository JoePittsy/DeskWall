using System.IO;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;
using Xunit;

namespace DeskWall.Designer.Tests;

/// <summary>Owner feedback 2026-09-21: the gallery cards render on a plain background, not on a crop
/// of the base photo. (The centre preview is the other way round and keeps the photo - that is
/// <c>PreviewRendererTests</c>.)</summary>
public class CardRendererTests
{
    private static WidgetTemplate Template()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "card-renderer");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.json");
        File.WriteAllText(path, """
        {
          "version": 1, "name": "Note", "description": "A word.", "size": [172, 40],
          "sources": [],
          "components": [ { "type": "text", "id": "word", "rect": [0, 0, 172, 40], "text": "hello", "size": 24 } ],
          "knobs": []
        }
        """);
        return WidgetTemplate.Load(path);
    }

    [Fact]
    public void A_Card_Is_The_Widget_On_Flat_Grey()
    {
        var card = CardRenderer.Render(Template(), ValueTree.Empty);

        Assert.Equal(CardRenderer.CardWidth, card.Width);
        Assert.Equal(40 + CardRenderer.VerticalPadding * 2, card.Height);
        Assert.Equal(card.Width * card.Height * 4, card.Bgra.Length);

        // Every corner is background: the widget is 172 px centred in 306, so nothing reaches them.
        foreach (var (x, y) in new[] { (0, 0), (card.Width - 1, 0), (0, card.Height - 1), (card.Width - 1, card.Height - 1) })
        {
            var i = y * card.Width * 4 + x * 4;
            Assert.Equal(CardRenderer.Background.B, card.Bgra[i]);
            Assert.Equal(CardRenderer.Background.G, card.Bgra[i + 1]);
            Assert.Equal(CardRenderer.Background.R, card.Bgra[i + 2]);
        }
    }

    /// <summary>The widget itself is still drawn, and drawn by Core: a card of nothing but grey
    /// would pass the test above and be useless.</summary>
    [Fact]
    public void The_Widget_Is_Drawn_On_Top_Of_It()
    {
        var card = CardRenderer.Render(Template(), ValueTree.Empty);

        var background = 0;
        for (var i = 0; i < card.Bgra.Length; i += 4)
            if (card.Bgra[i] == CardRenderer.Background.B && card.Bgra[i + 1] == CardRenderer.Background.G
                && card.Bgra[i + 2] == CardRenderer.Background.R) background++;

        Assert.True(background < card.Width * card.Height, "the card is nothing but background: the widget did not draw");
    }
}
