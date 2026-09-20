using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using Xunit;

file sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

public class SourceFactoryTests
{
    [Fact]
    public void Creates_Known_Types_And_Rejects_Unknown()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        Assert.IsType<TimeSource>(SourceFactory.Create(new() { Name = "t", Type = "time" }, clock));
        var d = Assert.IsType<DisksSource>(SourceFactory.Create(new() { Name = "d", Type = "disks", EverySeconds = 120 }, clock));
        Assert.Equal(TimeSpan.FromSeconds(120), d.Every);
        Assert.Throws<NotSupportedException>(() => SourceFactory.Create(new() { Name = "x", Type = "gauge" }, clock));
    }
}
