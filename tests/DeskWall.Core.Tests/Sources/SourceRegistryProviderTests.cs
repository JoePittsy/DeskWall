using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Sources;

file sealed class ProviderTestSource(string name, TimeSpan every) : PeriodicSource(name, every)
{
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(ValueTree.Empty);
}

/// <summary>Spec section 3: there is no event source type. A pushed provider is one more entry in
/// the flat map of name to values that Tree() already projects, so a layout binds build.data.status
/// with nothing declared anywhere.</summary>
public class SourceRegistryProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);

    private static RecordValue Rec(string key, string value)
        => new(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { [key] = new TextValue(value) });

    [Fact]
    public void A_Provider_Appears_In_The_Tree_Under_Its_Name()
    {
        var reg = new SourceRegistry();
        reg.SetProvider("build", Rec("status", "green"));

        var tree = reg.Tree();

        var build = Assert.IsType<RecordValue>(tree.Get("build"));
        Assert.Equal("green", ((TextValue)build.Get("status")!).Text);
    }

    [Fact]
    public void A_Provider_Appears_In_The_Schedule_Aware_Tree_Too()
    {
        var reg = new SourceRegistry();
        reg.SetProvider("build", Rec("status", "green"));

        Assert.True(reg.Tree([], T0).Fields.ContainsKey("build"));
    }

    [Fact]
    public void RemoveProvider_Takes_It_Back_Out()
    {
        var reg = new SourceRegistry();
        reg.SetProvider("build", Rec("status", "green"));
        reg.RemoveProvider("build");

        Assert.False(reg.Tree().Fields.ContainsKey("build"));
        reg.RemoveProvider("never-heard-of-it");                    // silent about an unknown one
    }

    [Fact]
    public void A_Layout_Source_Of_The_Same_Name_Wins_And_The_Provider_Is_Not_Merged()
    {
        var reg = new SourceRegistry();
        reg.SetProvider("weather", Rec("pushed", "yes"));
        reg.Set(SourceSnapshot.Initial("weather").Succeeded(Rec("polled", "yes"), T0));

        var weather = Assert.IsType<RecordValue>(reg.Tree().Get("weather"));
        Assert.Equal("yes", ((TextValue)weather.Get("polled")!).Text);
        Assert.Null(weather.Get("pushed"));                         // not merged, replaced

        var s = new ProviderTestSource("weather", TimeSpan.FromMinutes(5));
        var judged = Assert.IsType<RecordValue>(reg.Tree([s], T0).Get("weather"));
        Assert.Null(judged.Get("pushed"));
    }

    /// <summary>A declared source that has not refreshed yet still owns the name: letting the
    /// provider through for one tick and then swapping it out would flash on the wallpaper.</summary>
    [Fact]
    public void A_Declared_Source_Owns_Its_Name_Before_Its_First_Refresh()
    {
        var reg = new SourceRegistry();
        reg.SetProvider("weather", Rec("pushed", "yes"));
        var s = new ProviderTestSource("weather", TimeSpan.FromMinutes(5));

        Assert.False(reg.Tree([s], T0).Fields.ContainsKey("weather"));
    }

    [Fact]
    public void A_Provider_Is_Not_Subject_To_Schedule_Staleness()
    {
        var reg = new SourceRegistry { StaleAfter = 3 };
        reg.SetProvider("build", Rec("status", "green"));
        var s = new ProviderTestSource("weather", TimeSpan.FromMinutes(5));
        reg.Set(SourceSnapshot.Initial("weather").Succeeded(Rec("temp", "9"), T0));

        // A week later the polled source is long past its three schedules and gone; the pushed
        // provider has no schedule to fall behind, so it is still published (spec section 5:
        // expectEvery, which would turn staleness back on for it, is phase 2).
        var tree = reg.Tree([s], T0.AddDays(7));
        Assert.True(tree.Fields.ContainsKey("build"));
        Assert.False(tree.Fields.ContainsKey("weather"));
    }

    [Fact]
    public void ProviderNames_Lets_The_Caller_Report_A_Clash()
    {
        var reg = new SourceRegistry();
        reg.SetProvider("build", Rec("status", "green"));
        Assert.Equal(new[] { "build" }, reg.ProviderNames);
    }
}
