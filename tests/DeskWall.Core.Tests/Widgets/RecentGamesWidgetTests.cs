using DeskWall.Core.Layout;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using DeskWall.Core.Widgets;
using Xunit;

namespace DeskWall.Core.Tests.Widgets;

/// <summary><c>layouts/widgets/recent-games.json</c>'s Count knob draws exactly that many covers,
/// however wide its repeater is (#41). Before the knob trimmed with a <c>take</c> slice it set only
/// the repeater's width and let overflow drop the rest, so widening the part drew more games than
/// the knob said.</summary>
public class RecentGamesWidgetTests
{
    private static RecordValue Feed(int n)
    {
        var games = Enumerable.Range(0, n).Select(i => new RecordValue(new Dictionary<string, Value>
        {
            ["id"] = new TextValue("g" + i),
            ["name"] = new TextValue("Game " + i),
            ["cover"] = new TextValue(@"C:\nowhere\g" + i + ".png"),
            ["launch"] = new TextValue("notepad.exe"),
        })).ToList();
        var json = new RecordValue(new Dictionary<string, Value> { ["games"] = new ListValue(games, "id") });
        return ValueTree.Of(("hearth", new RecordValue(new Dictionary<string, Value> { ["json"] = json })));
    }

    private static IReadOnlyList<Resolved> Resolve(string? count, int feed = 8, string? widen = null)
    {
        var copy = new WidgetCopy { Id = "recent-games-1", Widget = "recent-games", X = 2400, Y = 600 };
        if (count is not null) copy.Knobs["count"] = count;
        if (widen is not null) copy.Overrides["components.games.w"] = PropertyValue.Literal(widen);
        var layout = new LayoutFile { Version = 2, BaseImage = "wall.jpg", Sources = [], Components = [], Copies = [copy] };
        var e = WidgetExpander.Expand(layout, WidgetCatalog.Finder(Path.Combine(Repo.Root, "layouts", "widgets")));
        Assert.Empty(e.Problems);
        return LayoutResolver.Resolve(e.Layout, Feed(feed));
    }

    private static string Choice(int n)
        => WidgetTemplate.Load(Path.Combine(Repo.Root, "layouts", "widgets", "recent-games.json"))
            .Knobs.Single(k => k.Id == "count").Choices!.Single(c => c.StartsWith(n + "||", StringComparison.Ordinal));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Count_Draws_That_Many_Covers(int n)
    {
        var resolved = Resolve(Choice(n));
        Assert.Equal(n, resolved.OfType<ResolvedImage>().Count());
        Assert.Equal(Enumerable.Range(8, n), resolved.OfType<ResolvedShortcut>().Select(s => s.Slot));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public void Widening_The_Repeater_Does_Not_Draw_More_Than_Count(int? n)
    {
        var resolved = Resolve(n is null ? null : Choice(n.Value), widen: "1016");
        Assert.Equal(n ?? 4, resolved.OfType<ResolvedImage>().Count());
        Assert.Equal(n ?? 4, resolved.OfType<ResolvedShortcut>().Count());
    }

    [Fact]
    public void A_Short_Feed_Draws_What_There_Is()
        => Assert.Equal(2, Resolve(null, feed: 2).OfType<ResolvedImage>().Count());
}
