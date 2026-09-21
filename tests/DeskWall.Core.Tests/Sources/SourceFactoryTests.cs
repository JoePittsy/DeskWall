using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

file sealed class CountingSource(bool throwOnDispose) : PeriodicSource("d", TimeSpan.FromMinutes(1)), IDisposable
{
    public int Disposals;
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
        => new(new RecordValue(new Dictionary<string, Value>()));
    public void Dispose()
    {
        Disposals++;
        if (throwOnDispose) throw new InvalidOperationException("a handle that will not let go");
    }
}

file sealed class PlainSource() : PeriodicSource("p", TimeSpan.FromMinutes(1))
{
    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
        => new(new RecordValue(new Dictionary<string, Value>()));
}

public class SourceFactoryTests
{
    [Fact]
    public void Creates_Known_Types_And_Rejects_Unknown()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        Assert.IsType<TimeSource>(SourceFactory.Create(new() { Name = "t", Type = "time" }, clock));
        var d = Assert.IsType<DisksSource>(SourceFactory.Create(new() { Name = "d", Type = "disks", EverySeconds = 120 }, clock));
        Assert.Equal(TimeSpan.FromSeconds(120), d.Every);
        Assert.IsType<SystemSource>(SourceFactory.Create(new() { Name = "s", Type = "system" }, clock));
        Assert.IsType<FileSource>(SourceFactory.Create(new() { Name = "f", Type = "file", Settings = new() { ["path"] = "x.txt" } }, clock));
        Assert.IsType<HttpSource>(SourceFactory.Create(new() { Name = "h", Type = "http", Settings = new() { ["url"] = "https://x/" } }, clock));
        Assert.IsType<RssSource>(SourceFactory.Create(new() { Name = "r", Type = "rss", Settings = new() { ["url"] = "https://x/feed" } }, clock));
        Assert.IsType<CommandSource>(SourceFactory.Create(new() { Name = "c", Type = "command", Settings = new() { ["command"] = "cmd.exe" } }, clock));
        Assert.Throws<NotSupportedException>(() => SourceFactory.Create(new() { Name = "x", Type = "gauge" }, clock));
    }

    [Fact]
    public void Hardware_Type_Creates_HardwareSource_With_Defaults()
    {
        var def = new SourceDef { Name = "hw", Type = "hardware" };
        var s = SourceFactory.Create(def, new FakeClock(DateTimeOffset.UnixEpoch));
        var hw = Assert.IsType<DeskWall.Core.Sources.Hardware.HardwareSource>(s);
        Assert.Equal(TimeSpan.FromSeconds(60), hw.Every);
    }

    /// <summary>Sources outlive nothing: the daemon replaces the whole set on every layout change,
    /// so whoever owns a set has to let go of the ones that hold a timer or a native library.</summary>
    [Fact]
    public void DisposeAll_Disposes_Every_Disposable_Source_And_Survives_One_That_Throws()
    {
        var a = new CountingSource(throwOnDispose: false);
        var bad = new CountingSource(throwOnDispose: true);
        var b = new CountingSource(throwOnDispose: false);

        SourceFactory.DisposeAll([a, bad, b, new PlainSource()]);

        Assert.Equal(1, a.Disposals);
        Assert.Equal(1, bad.Disposals);
        Assert.Equal(1, b.Disposals);   // the thrower did not stop the rest
        SourceFactory.DisposeAll(null); // and null is simply nothing to do
    }
}
