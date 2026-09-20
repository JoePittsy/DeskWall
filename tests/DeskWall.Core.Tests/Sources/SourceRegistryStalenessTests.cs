using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

namespace DeskWall.Core.Tests.Sources;

file sealed class EverySource(string name, TimeSpan every) : PeriodicSource(name, every)
{
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct) => new(ValueTree.Empty);
}

/// <summary>Finding 15, spec 3.2: a source that keeps failing used to publish its last good values
/// forever, so a disk bar could sit at a number from last Tuesday with nothing on screen saying so.
/// After StaleAfter missed schedules the values stop being published and the binding falls back.</summary>
public class SourceRegistryStalenessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 14, 0, 0, TimeSpan.Zero);

    private static RecordValue Some(string k) => new(new Dictionary<string, Value> { [k] = new TextValue(k) });

    [Fact]
    public void Default_Registry_Publishes_Stale_Values_Forever()
    {
        var reg = new SourceRegistry();
        var s = new EverySource("a", TimeSpan.FromMinutes(5));
        reg.Set(SourceSnapshot.Initial("a").Succeeded(Some("v"), T0));
        // StaleAfter defaults to 0: the Phase 1 behaviour, unchanged.
        Assert.True(reg.Tree([s], T0.AddDays(7)).Fields.ContainsKey("a"));
    }

    [Fact]
    public void Values_Survive_Up_To_StaleAfter_Schedules_And_Are_Dropped_Past_It()
    {
        var reg = new SourceRegistry { StaleAfter = 3 };
        var s = new EverySource("a", TimeSpan.FromMinutes(5));
        reg.Set(SourceSnapshot.Initial("a").Succeeded(Some("v"), T0));

        Assert.True(reg.Tree([s], T0.AddMinutes(14)).Fields.ContainsKey("a"));    // two schedules late: still trusted
        Assert.True(reg.Tree([s], T0.AddMinutes(15)).Fields.ContainsKey("a"));    // exactly three: the boundary is inclusive
        Assert.False(reg.Tree([s], T0.AddMinutes(16)).Fields.ContainsKey("a"));   // past it: gone
    }

    [Fact]
    public void A_Source_That_Never_Succeeded_Is_Simply_Absent()
    {
        var reg = new SourceRegistry { StaleAfter = 3 };
        var s = new EverySource("a", TimeSpan.FromMinutes(5));
        Assert.False(reg.Tree([s], T0).Fields.ContainsKey("a"));
    }

    [Fact]
    public void Transitions_Are_Reported_Once_Each_Way()
    {
        var reg = new SourceRegistry { StaleAfter = 3 };
        var s = new EverySource("a", TimeSpan.FromMinutes(5));
        var events = new List<string>();
        reg.StaleChanged += (name, stale) => events.Add($"{name}={stale}");
        reg.Set(SourceSnapshot.Initial("a").Succeeded(Some("v"), T0));

        reg.Tree([s], T0.AddMinutes(1));
        Assert.Empty(events);

        reg.Tree([s], T0.AddMinutes(30));
        reg.Tree([s], T0.AddMinutes(31));
        reg.Tree([s], T0.AddMinutes(32));
        Assert.Equal(["a=True"], events);   // once per transition, not once per tick

        reg.Set(SourceSnapshot.Initial("a").Succeeded(Some("v"), T0.AddMinutes(33)));
        reg.Tree([s], T0.AddMinutes(33));
        reg.Tree([s], T0.AddMinutes(34));
        Assert.Equal(["a=True", "a=False"], events);
    }

    [Fact]
    public void A_Source_The_Caller_Did_Not_List_Is_Published_Unjudged()
    {
        var reg = new SourceRegistry { StaleAfter = 3 };
        reg.Set(SourceSnapshot.Initial("orphan").Succeeded(Some("v"), T0));
        Assert.True(reg.Tree([], T0.AddDays(7)).Fields.ContainsKey("orphan"));
    }

    [Fact]
    public void An_Always_Due_Source_Can_Never_Be_Stale()
    {
        // NextDue == last means "no schedule of its own"; dividing by that interval is meaningless.
        var reg = new SourceRegistry { StaleAfter = 3 };
        var s = new EverySource("a", TimeSpan.Zero);
        reg.Set(SourceSnapshot.Initial("a").Succeeded(Some("v"), T0));
        Assert.True(reg.Tree([s], T0.AddDays(7)).Fields.ContainsKey("a"));
    }
}
