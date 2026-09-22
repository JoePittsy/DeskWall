using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

file sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

/// <summary>`stream: true` is what makes a cheap always-on producer possible without a process
/// spawn per tick: the process stays up and every line it prints becomes the source's values.
/// The scripts below are .cmd files because cmd.exe starts in milliseconds; `ping -n` is the sleep
/// that works with stdout redirected (`timeout /t` refuses it).</summary>
public class StreamingCommandSourceTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "deskwall-tests", "stream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Script(string dir, string name, string body)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, body.ReplaceLineEndings("\r\n"));
        return p;
    }

    private static SourceDef Def(string script, string? parse = null, double? timeout = null)
    {
        var d = new SourceDef { Name = "streamer", Type = "command" };
        d.Settings["command"] = Cmd;
        d.Settings["args"] = $"/c \"{script}\"";
        d.Settings["stream"] = "true";
        if (parse is not null) d.Settings["parse"] = parse;
        if (timeout is not null) d.Settings["timeout"] = timeout.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return d;
    }

    private static ISource Build(SourceDef def) => SourceFactory.Create(def, new FixedClock(DateTimeOffset.UnixEpoch), NoSecrets());

    private static Secrets NoSecrets() => new(Path.Combine(Path.GetTempPath(), "deskwall-tests", "no-such-secrets.json"));

    /// <summary>A script that prints then stays up for a minute, so the source is exercised while
    /// its process is genuinely resident.</summary>
    private static string Resident(string dir, params string[] lines)
        => Script(dir, "s.cmd", "@echo off\r\n" + string.Join("\r\n", lines) + "\r\nping -n 60 127.0.0.1 >nul\r\n");

    [Fact]
    public async Task A_Printed_Line_Becomes_The_Current_Values_And_Signals()
    {
        var dir = Dir();
        var src = Build(Def(Resident(dir, """echo {"n": 1}"""), parse: "json"));
        using var disp = (IDisposable)src;
        using var fired = new ManualResetEventSlim(false);
        ((ISignalSource)src).Changed += _ => fired.Set();

        await src.RefreshAsync(default);   // starts the process
        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));
        var v = await src.RefreshAsync(default);

        Assert.Equal(1, ((NumberValue)((RecordValue)v.Get("json")!).Get("n")!).Number);
        Assert.True(((BoolValue)v.Get("running")!).Flag);
    }

    /// <summary>Nothing has been printed yet is not a failure. It must not be: a failed refresh puts
    /// the source on Scheduler's back-off, and while a source is backed off the scheduler ignores
    /// NextDue entirely - so the wake the first line raises would find the source not due and the
    /// push path would never start.</summary>
    [Fact]
    public async Task Before_The_First_Line_The_Record_Is_Empty_Rather_Than_A_Failure()
    {
        var dir = Dir();
        var src = Build(Def(Script(dir, "s.cmd", "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n"), parse: "json"));
        using var disp = (IDisposable)src;

        var v = await src.RefreshAsync(default);

        Assert.Null(v.Get("json"));
        Assert.Null(v.Get("text"));
        Assert.Equal(0, ((NumberValue)v.Get("badLines")!).Number);
    }

    /// <summary>A producer that prints a diagnostic line, or half a line because it was killed
    /// mid-write, must not blank the widget. The bad line is counted so the user can see it
    /// happened at all.</summary>
    [Fact]
    public async Task A_Line_That_Does_Not_Parse_Is_Skipped_And_Counted()
    {
        var dir = Dir();
        var script = Resident(dir, """echo {"n": 1}""", "echo this is not json", """echo {"n": 2}""");
        var src = Build(Def(script, parse: "json"));
        using var disp = (IDisposable)src;
        var signals = 0;
        using var second = new ManualResetEventSlim(false);
        ((ISignalSource)src).Changed += _ => { if (Interlocked.Increment(ref signals) == 2) second.Set(); };

        await src.RefreshAsync(default);
        Assert.True(second.Wait(TimeSpan.FromSeconds(10)));
        var v = await src.RefreshAsync(default);

        Assert.Equal(2, ((NumberValue)((RecordValue)v.Get("json")!).Get("n")!).Number);
        Assert.Equal(1, ((NumberValue)v.Get("badLines")!).Number);
        Assert.Equal(2, signals);   // the bad line raised none
    }

    /// <summary>The process exiting is not fatal, and a process that exits at once and keeps doing
    /// it must not spin the machine. Scheduler.BackOff is the same doubling a failing pull source
    /// gets; without it this is a fork bomb at whatever rate cmd.exe can start.</summary>
    [Fact]
    public async Task An_Immediately_Exiting_Process_Is_Restarted_Without_Spinning()
    {
        var dir = Dir();
        var src = Build(Def(Script(dir, "s.cmd", "@echo off\r\nexit /b 0\r\n"), parse: "text"));
        using var disp = (IDisposable)src;
        var streaming = (StreamingCommandSource)src;

        await src.RefreshAsync(default);
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 250 ms doubling gives starts at roughly 0, 0.25, 0.75, 1.75, 3.75 s. A tight loop would
        // be thousands; the upper bound is what this test is for.
        Assert.InRange(streaming.Starts, 2, 10);
    }

    /// <summary>Dispose kills the whole tree, as the non-streaming source's timeout path does. The
    /// grandchild is what makes that "the whole tree" rather than "the process we started": killing
    /// only the cmd.exe we launched leaves it appending to its file.
    /// <para>The child bounds its own life at about thirty seconds. A test that leaves an immortal
    /// process behind when it fails is worse than no test: the orphan inherits the test host's
    /// stdout pipe and nothing downstream ever sees end-of-file.</para></summary>
    [Fact]
    public async Task Dispose_Kills_The_Whole_Process_Tree()
    {
        var dir = Dir();
        var marker = Path.Combine(dir, "alive.txt");
        var child = Script(dir, "child.cmd",
            $"@echo off\r\nfor /l %%i in (1,1,15) do (\r\n  echo x >> \"{marker}\"\r\n  ping -n 3 127.0.0.1 >nul\r\n)\r\n");
        var script = Script(dir, "s.cmd", $"@echo off\r\necho {{\"n\": 1}}\r\n\"{Cmd}\" /c \"{child}\"\r\n");
        var src = Build(Def(script, parse: "json"));
        using var fired = new ManualResetEventSlim(false);
        ((ISignalSource)src).Changed += _ => fired.Set();
        await src.RefreshAsync(default);
        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(File.Exists(marker), "the grandchild never started; the test proves nothing");

        ((IDisposable)src).Dispose();
        ((IDisposable)src).Dispose();   // idempotent
        await Task.Delay(TimeSpan.FromSeconds(1));
        var settled = new FileInfo(marker).Length;
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(settled, new FileInfo(marker).Length);
    }

    /// <summary>There is no single run to time out, so `timeout` does not apply. Documented rather
    /// than silently ignored.</summary>
    [Fact]
    public async Task A_Timeout_Does_Not_Apply_To_A_Streaming_Command()
    {
        var dir = Dir();
        var src = Build(Def(Script(dir, "s.cmd", "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n"), parse: "json", timeout: 1));
        using var disp = (IDisposable)src;

        await src.RefreshAsync(default);
        await Task.Delay(TimeSpan.FromSeconds(2));   // well past the timeout, with nothing printed
        var v = await src.RefreshAsync(default);     // must not throw

        Assert.True(((BoolValue)v.Get("running")!).Flag);
    }

    /// <summary>An exception crossing out of the reader thread is an unhandled exception on a
    /// background thread, which takes the whole daemon down. The throwing handler below stands in
    /// for anything a subscriber does wrong; if it escaped, this test would not report a failure,
    /// it would kill the test host.</summary>
    [Fact]
    public async Task Nothing_Escapes_The_Reader_Thread()
    {
        var dir = Dir();
        var src = Build(Def(Resident(dir, """echo {"n": 1}""", """echo {"n": 2}"""), parse: "json"));
        using var disp = (IDisposable)src;
        var signals = 0;
        using var second = new ManualResetEventSlim(false);
        ((ISignalSource)src).Changed += _ =>
        {
            if (Interlocked.Increment(ref signals) == 2) second.Set();
            throw new InvalidOperationException("a subscriber that misbehaves");
        };

        await src.RefreshAsync(default);
        Assert.True(second.Wait(TimeSpan.FromSeconds(10)));
        var v = await src.RefreshAsync(default);

        Assert.Equal(2, ((NumberValue)((RecordValue)v.Get("json")!).Get("n")!).Number);
    }

    /// <summary>Signalling is a claim of due-ness, and the scheduler is what decides; a streaming
    /// source that says "not due" on its own wake would wake the machine for nothing.</summary>
    [Fact]
    public async Task A_Pending_Line_Makes_The_Source_Due_Now()
    {
        var dir = Dir();
        var src = Build(Def(Resident(dir, """echo {"n": 1}"""), parse: "json"));
        using var disp = (IDisposable)src;
        using var fired = new ManualResetEventSlim(false);
        ((ISignalSource)src).Changed += _ => fired.Set();
        var t0 = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

        await src.RefreshAsync(default);
        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal(t0, src.NextDue(t0, t0));
        await src.RefreshAsync(default);
        Assert.Equal(t0.AddSeconds(600), src.NextDue(t0, t0));
    }

    [Fact]
    public void Without_Stream_The_Command_Source_Is_Unchanged()
    {
        var d = new SourceDef { Name = "plain", Type = "command" };
        d.Settings["command"] = Cmd;
        d.Settings["args"] = "/c echo hi";
        Assert.IsType<CommandSource>(Build(d));
    }

    [Fact]
    public void Stream_Builds_A_Streaming_Source()
    {
        var dir = Dir();
        var src = Build(Def(Resident(dir, "echo hi"), parse: "text"));
        using var disp = (IDisposable)src;
        Assert.IsType<StreamingCommandSource>(src);
    }
}
