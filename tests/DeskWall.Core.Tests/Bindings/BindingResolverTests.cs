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
}
