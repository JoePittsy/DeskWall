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

    /// <summary>Finding 2: Write caught only IOException, so a read-only or ACL-denied runtime dir threw
    /// UnauthorizedAccessException out of the tick's own catch handler and took the daemon with it.
    /// Nothing on RollingLog may throw, whatever path it was given or exception it is handed.</summary>
    [Fact]
    public void Logging_Never_Throws()
    {
        // A path whose parent is a file, not a directory: CreateDirectory throws IOException.
        var file = Temp("x.log");
        File.WriteAllText(file, "not a directory");
        var log = new RollingLog(Path.Combine(file, "t.log"));
        log.Info("hello");
        log.Warn("careful");
        log.Error("boom", new HostileException());
        Assert.Equal("boom", log.LastError);   // the exception's own Message threw; the bare message stands

        // An invalid path is a different failure again (ArgumentException, not IOException).
        var bad = new RollingLog("\0:" + Path.DirectorySeparatorChar + "nope" + Path.DirectorySeparatorChar + "t.log");
        bad.Info("hello");
        bad.Error("boom", new InvalidOperationException("why"));
        Assert.Equal("boom: InvalidOperationException: why", bad.LastError);
    }

    /// <summary>An exception whose Message cannot be read. Contrived, but Error must survive it.</summary>
    private sealed class HostileException : Exception
    {
        public override string Message => throw new NotSupportedException("no message for you");
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
