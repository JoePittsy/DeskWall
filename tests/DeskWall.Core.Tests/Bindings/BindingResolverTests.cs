using DeskWall.Core.Bindings;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Bindings;

public class BindingResolverTests
{
    private static RecordValue Root()
    {
        var c = new RecordValue(new Dictionary<string, Value> { ["letter"] = new TextValue("C"), ["free"] = new NumberValue(120_000_000_000) });
        var disks = new RecordValue(new Dictionary<string, Value> { ["drives"] = new ListValue([c], "letter") });
        var time = new RecordValue(new Dictionary<string, Value> { ["now"] = new TimeValue(new DateTimeOffset(2026, 9, 20, 14, 32, 0, TimeSpan.Zero)) });
        return ValueTree.Of(("disks", disks), ("time", time));
    }

    [Fact]
    public void Resolves_Key_Lookup_And_Formats()
        => Assert.Equal("120,000,000,000 GB free", BindingResolver.ResolveText(Binding.Parse("disks.drives[C].free | \"{0:N0} GB free\""), Root()));

    [Fact]
    public void Resolves_Time_Format()
        => Assert.Equal("14:32", BindingResolver.ResolveText(Binding.Parse("time.now | HH:mm"), Root()));

    [Fact]
    public void Resolves_Index()
        => Assert.Equal("C", BindingResolver.ResolveText(Binding.Parse("disks.drives[0].letter"), Root()));

    [Theory]
    [InlineData("disks.drives[Z].free")]
    [InlineData("disks.drives[5].free")]
    [InlineData("nope.now")]
    [InlineData("time.now.hour")]
    public void Missing_Returns_Null(string s) => Assert.Null(BindingResolver.Resolve(Binding.Parse(s), Root()));

    private static RecordValue Games(int n)
    {
        var items = Enumerable.Range(0, n)
            .Select(i => new RecordValue(new Dictionary<string, Value> { ["id"] = new TextValue("g" + i) }))
            .ToList();
        return ValueTree.Of(("hearth", new RecordValue(new Dictionary<string, Value> { ["games"] = new ListValue(items, "id") })));
    }

    private static string[] Ids(Value? v)
        => ((ListValue)v!).Items.Select(r => r.Get("id")!.ToText(null)).ToArray();

    [Fact]
    public void Take_Keeps_The_First_N()
        => Assert.Equal(["g0", "g1", "g2"], Ids(BindingResolver.Resolve(Binding.Parse("hearth.games[..3]"), Games(6))));

    [Fact]
    public void Take_More_Than_There_Are_Keeps_Them_All()
        => Assert.Equal(["g0", "g1"], Ids(BindingResolver.Resolve(Binding.Parse("hearth.games[..8]"), Games(2))));

    [Fact]
    public void Skip_And_Range()
    {
        Assert.Equal(["g4", "g5"], Ids(BindingResolver.Resolve(Binding.Parse("hearth.games[4..]"), Games(6))));
        Assert.Equal(["g1", "g2"], Ids(BindingResolver.Resolve(Binding.Parse("hearth.games[1..3]"), Games(6))));
        Assert.Empty(Ids(BindingResolver.Resolve(Binding.Parse("hearth.games[9..]"), Games(6))));
        Assert.Empty(Ids(BindingResolver.Resolve(Binding.Parse("hearth.games[..0]"), Games(6))));
    }

    [Fact]
    public void Slice_Keeps_Key_Lookup_And_Indexes_Into_The_Slice()
    {
        Assert.Equal("g2", BindingResolver.ResolveText(Binding.Parse("hearth.games[2..][0].id"), Games(6)));
        Assert.Equal("g1", BindingResolver.ResolveText(Binding.Parse("hearth.games[..3][g1].id"), Games(6)));
        Assert.Null(BindingResolver.Resolve(Binding.Parse("hearth.games[..3][g4]"), Games(6)));
    }

    [Fact]
    public void Slice_Of_A_Non_List_Is_Null()
        => Assert.Null(BindingResolver.Resolve(Binding.Parse("time.now[..2]"), Root()));
}
