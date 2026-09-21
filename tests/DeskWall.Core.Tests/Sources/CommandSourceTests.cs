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

    /// <summary>Found building a "3 in Downloads" widget: cmd's `find /c` prints "3\r\n", and a text
    /// component bound to `cmd.text | "{0} in Downloads"` wrapped onto two lines because the newline
    /// was inside the value. A console program's trailing line break is how it ends its output, not
    /// part of the value; anything else (interior lines, leading spaces) is kept as printed.</summary>
    [Fact]
    public async Task Text_Drops_The_Trailing_Line_Break_Only()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c echo  two words"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Equal(" two words", ((TextValue)v.Get("text")!).Text);

        var multi = await CommandSource.FromDef(Def("cmd.exe", "/c (echo one & echo two)"), new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Equal("one \r\ntwo", ((TextValue)multi.Get("text")!).Text);
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

    /// <summary>The Downloads and Progress widgets carried an absolute
    /// C:\Users\<me>\AppData\Local\DeskWall\scripts inside them, so the template was not
    /// portable. `command` and `workingDir` understand `runtime:` as well as %ENV%.</summary>
    [Fact]
    public void Command_And_WorkingDir_Understand_The_Runtime_Prefix()
    {
        var src = CommandSource.FromDef(
            Def(@"runtime:scripts\progress.ps1", "-x", ("workingDir", "runtime:scripts")),
            new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets());
        Assert.Equal(DeskWall.Core.Paths.InRuntime("scripts", "progress.ps1"), src.Command);
        Assert.Equal(DeskWall.Core.Paths.InRuntime("scripts"), src.WorkingDir);
    }

    [Fact]
    public void An_Ordinary_Command_Is_Untouched_And_Env_Still_Expands()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        Assert.Equal("cmd.exe", CommandSource.FromDef(Def("cmd.exe", "/c echo x"), clock, NoSecrets()).Command);
        Assert.Equal("", CommandSource.FromDef(Def("cmd.exe", "/c echo x"), clock, NoSecrets()).WorkingDir);
        var env = CommandSource.FromDef(Def(@"%SystemRoot%\System32\cmd.exe", "/c echo x", ("workingDir", "%SystemRoot%")), clock, NoSecrets());
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), env.Command, ignoreCase: true);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), env.WorkingDir);
    }

    /// <summary>End to end: the process really does start in the runtime dir.</summary>
    [Fact]
    public async Task A_Runtime_WorkingDir_Is_Where_The_Process_Actually_Runs()
    {
        var v = await CommandSource.FromDef(Def("cmd.exe", "/c cd", ("workingDir", "runtime:")),
            new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets()).RefreshAsync(default);
        Assert.Equal(DeskWall.Core.Paths.RuntimeDir.TrimEnd('\\'),
            ((TextValue)v.Get("text")!).Text.Trim().TrimEnd('\\'), ignoreCase: true);
    }
}
