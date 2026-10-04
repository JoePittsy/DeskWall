using DeskWall.Core.Bindings;
using Xunit;

namespace DeskWall.Core.Tests.Bindings;

public class BindingParserTests
{
    [Fact]
    public void Parses_Path_And_QuotedFormat()
    {
        var b = Binding.Parse("disks.drives[C].free | \"{0:N0} GB free\"");
        Assert.Collection(b.Path,
            s => Assert.Equal(new NameSegment("disks"), s),
            s => Assert.Equal(new NameSegment("drives"), s),
            s => Assert.Equal(new KeySegment("C"), s),
            s => Assert.Equal(new NameSegment("free"), s));
        Assert.Equal("{0:N0} GB free", b.Format);
    }

    [Fact]
    public void Parses_Index_And_BareFormat()
    {
        var b = Binding.Parse("time.now|HH:mm");
        Assert.Equal("HH:mm", b.Format);
        var c = Binding.Parse("steam.json.games[0].appid");
        Assert.Equal(new IndexSegment(0), c.Path[3]);
        Assert.Null(c.Format);
    }

    [Theory]
    [InlineData("")]
    [InlineData("time.")]
    [InlineData("time[")]
    [InlineData("time..now")]
    [InlineData("9lives")]
    [InlineData("games[..]")]
    [InlineData("games[3..1]")]
    public void Rejects_Malformed(string text) => Assert.Throws<FormatException>(() => Binding.Parse(text));

    [Fact]
    public void ToString_RoundTrips()
    {
        const string s = "disks.drives[C].free | \"{0:N0} GB free\"";
        Assert.Equal(s, Binding.Parse(s).ToString());
    }

    [Theory]
    [InlineData("games[..4]", null, 4)]
    [InlineData("games[2..]", 2, null)]
    [InlineData("games[1..3]", 1, 3)]
    [InlineData("games[0..0]", 0, 0)]
    public void Parses_Slice(string text, int? start, int? end)
        => Assert.Equal(new SliceSegment(start, end), Binding.Parse(text).Path[1]);

    [Theory]
    [InlineData("games[a..b]")]
    [InlineData("games[-1..2]")]
    [InlineData("games[..{count}]")]
    public void Not_A_Slice_Stays_A_Key(string text) => Assert.IsType<KeySegment>(Binding.Parse(text).Path[1]);

    [Theory]
    [InlineData("hearth.json.games[..4]")]
    [InlineData("hearth.json.games[1..][0].cover | \"{0}\"")]
    [InlineData("hearth.json.games[..{count}]")]
    public void Slice_ToString_RoundTrips(string s) => Assert.Equal(s, Binding.Parse(s).ToString());
}
