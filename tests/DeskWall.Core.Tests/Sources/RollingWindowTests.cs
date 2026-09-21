using DeskWall.Core.Sources.Hardware;
using Xunit;

public class RollingWindowTests
{
    [Fact]
    public void Empty_Has_No_Average()
    {
        var w = new RollingWindow(3);
        Assert.Equal(0, w.Count);
        Assert.Null(w.Average);
    }

    [Fact]
    public void Averages_What_It_Holds()
    {
        var w = new RollingWindow(3);
        w.Add(0.2); w.Add(0.4);
        Assert.Equal(2, w.Count);
        Assert.Equal(0.3, w.Average!.Value, 9);
    }

    [Fact]
    public void Wraps_Dropping_The_Oldest()
    {
        var w = new RollingWindow(3);
        w.Add(1); w.Add(2); w.Add(3); w.Add(10);   // 1 falls out
        Assert.Equal(3, w.Count);
        Assert.Equal(5, w.Average!.Value, 9);
    }

    [Fact]
    public void Latest_Is_The_Last_Added()
    {
        var w = new RollingWindow(2);
        Assert.Null(w.Latest);
        w.Add(7); w.Add(8);
        Assert.Equal(8, w.Latest);
    }

    [Fact]
    public void Capacity_Below_One_Is_One()
    {
        var w = new RollingWindow(0);
        w.Add(4); w.Add(5);
        Assert.Equal(1, w.Count);
        Assert.Equal(5, w.Average);
    }
}
