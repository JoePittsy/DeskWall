using System.Diagnostics;
using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }

public class CommandSourceTests
{
    private static Secrets NoSecrets() => new(Path.Combine(Path.GetTempPath(), "deskwall-tests", "none.json"));

    private static SourceDef Def(string command, string args, params (string k, string v)[] extra)
    {
        var d = new SourceDef { Name = "cmd", Type = "command", EverySeconds = 60 };
        d.Settings["command"] = command; d.Settings["args"] = args;
        foreach (var (k, v) in extra) d.Settings[k] = v;
        return d;
    }

    [Fact]
    public async Task Captures_Stdout_As_Text_And_ExitCode()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c echo hello & exit 3"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Equal("hello", ((TextValue)v.Get("text")!).Text.Trim());
        Assert.Equal(3, ((NumberValue)v.Get("exitCode")!).Number);
        Assert.IsType<TimeValue>(v.Get("ranAt"));
        Assert.Null(v.Get("stderr"));
    }

    /// <summary>Finding 15: a non-zero exit with nothing on stdout has no values to publish, and
    /// text = "" over the last good text is the partial-record-over-last-good spec 3.2 decides against.
    /// A non-zero exit that did print is still a success - scripts use the code as a flag, which the
    /// "echo hello &amp; exit 3" test above pins.</summary>
    [Fact]
    public async Task Nonzero_Exit_With_No_Output_Throws_So_The_Last_Good_Values_Stay()
    {
        var src = CommandSource.FromDef(Def("cmd.exe", "/c exit 4"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await src.RefreshAsync(default));
        Assert.Contains("exited 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_Stdout_Is_Parsed()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c echo {\"up\":5, \"name\": \"x\"}"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Equal(5, ((NumberValue)((RecordValue)v.Get("json")!).Get("up")!).Number);
    }

    [Fact]
    public async Task Stderr_Is_Captured()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c echo oops 1>&2"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Contains("oops", ((TextValue)v.Get("stderr")!).Text);
    }

    [Fact]
    public async Task Timeout_Kills_And_Throws()
    {
        var before = Process.GetProcessesByName("ping").Select(p => p.Id).ToHashSet();
        var src = CommandSource.FromDef(Def("cmd.exe", "/c ping -n 30 127.0.0.1 > nul", ("timeout", "1")), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets());
        await Assert.ThrowsAsync<TimeoutException>(async () => await src.RefreshAsync(default));

        // The kill happens Timeout + 1s after the process starts; give it a little more room, then
        // confirm no ping process we started (i.e. not present before this test) survives.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        List<int> ours;
        do
        {
            ours = Process.GetProcessesByName("ping").Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
            if (ours.Count == 0) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        Assert.Empty(ours);
    }

    [Fact]
    public void Missing_Command_Setting_Throws_On_Create()
        => Assert.Throws<ArgumentException>(() => CommandSource.FromDef(new SourceDef { Name = "c", Type = "command" }, new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()));
}
