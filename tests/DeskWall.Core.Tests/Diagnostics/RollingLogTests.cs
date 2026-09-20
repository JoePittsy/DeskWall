using DeskWall.Core.Diagnostics;
using Xunit;

public class RollingLogTests
{
    private static string Temp(string name) { var d = Path.Combine(Path.GetTempPath(), "deskwall-tests", "log-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return Path.Combine(d, name); }

    [Fact]
    public void Writes_Lines_With_Level_And_Tracks_LastError()
    {
        var p = Temp("t.log");
        var log = new RollingLog(p);
        log.Info("hello");
        log.Error("boom", new InvalidOperationException("why"));
        var lines = File.ReadAllLines(p);
        Assert.Equal(2, lines.Length);
        Assert.Contains("[INFO] hello", lines[0]);
        Assert.Contains("[ERROR] boom: InvalidOperationException: why", lines[1]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", lines[0]);
        Assert.Equal("boom: InvalidOperationException: why", log.LastError);
    }

    [Fact]
    public void Rolls_When_Over_MaxBytes()
    {
        var p = Temp("r.log");
        var log = new RollingLog(p, maxBytes: 500);
        for (var i = 0; i < 20; i++) log.Info(new string('x', 60));
        Assert.True(File.Exists(Path.ChangeExtension(p, ".1.log")));
        Assert.True(new FileInfo(p).Length < 500 + 120);
    }
}
