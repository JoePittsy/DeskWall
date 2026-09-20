using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DeskWall.Core.Tick;
using Xunit;
using Xunit.Abstractions;

namespace DeskWall.Core.Tests.Budget;

/// <summary>What one four-minute look at an idle daemon measured. Taken once for the whole class: the
/// three idle budgets are three questions about the same window, and paying for it three times would
/// put the suite past a quarter of an hour for nothing.</summary>
internal sealed record IdleSample(
    double ColdStartMs,
    long PrivateBytes,
    long WorkingSetBytes,
    int Handles,
    int Threads,
    double WindowCpuMs,
    double TickCpuMs,
    TimeSpan Window)
{
    /// <summary>Processor time the process spent NOT inside a tick: the number spec 1.2 says is zero.</summary>
    public double IdleCpuMs => Math.Max(0, WindowCpuMs - TickCpuMs);
}

/// <summary>Runs the daemon once and records everything the idle budgets ask about. Lazy, so a run
/// filtered down to the cold-start or tick budget never pays the four minutes.</summary>
internal static class IdleObservation
{
    /// <summary>Long enough for the first frame even with a cold base-image cache (the 3440x1440 raw
    /// dump has to be decoded and written the first time).</summary>
    private const int FirstFrameTimeoutMs = 60_000;

    private const int SampleAtMs = 180_000;    // spec 1.2 reads private bytes and handles after 3 min
    private const int WindowMs = 240_000;      // and the cpu window is 4 min

    private static readonly Lazy<IdleSample?> Observation =
        new(Measure, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when there is no published daemon to watch.</summary>
    public static IdleSample? Value => Observation.Value;

    private static IdleSample? Measure()
    {
        if (Published.Exe is not { } exe) return null;
        using var scratch = DaemonScratch.Create(exe, "clock-disks.json");
        var daemon = scratch.StartDaemon();
        var cold = DaemonScratch.WaitForFile(scratch.OutPath, daemon, FirstFrameTimeoutMs)
            ?? throw new InvalidOperationException(
                $"the daemon produced no {scratch.OutPath} within {FirstFrameTimeoutMs} ms" +
                (daemon.HasExited ? $" and exited with {daemon.ExitCode} (is another daemon already running?)" : ""));

        // The window starts at the first frame, not at Process.Start: the startup cost is the cold
        // start budget's business and counting it twice would make the idle budget unmeetable.
        var logFrom = scratch.ReadLogLines().Count;
        daemon.Refresh();
        var cpu0 = daemon.TotalProcessorTime;
        var start = Stopwatch.StartNew();

        Thread.Sleep(SampleAtMs);
        daemon.Refresh();
        var priv = daemon.PrivateMemorySize64;
        var ws = daemon.WorkingSet64;
        var handles = daemon.HandleCount;
        var threads = daemon.Threads.Count;

        Thread.Sleep(Math.Max(0, WindowMs - (int)start.ElapsedMilliseconds));
        daemon.Refresh();
        var windowCpu = (daemon.TotalProcessorTime - cpu0).TotalMilliseconds;
        var window = start.Elapsed;
        var tickCpu = scratch.LoggedTickCpuMs(logFrom);

        return new IdleSample(cold, priv, ws, handles, threads, windowCpu, tickCpu, window);
    }
}

/// <summary>Spec 1.2's cost table, measured against the published native-AOT daemon on the reference
/// machine. Excluded from the default `dotnet test` by <c>tests/deskwall.runsettings</c>; run with
/// <c>dotnet test --filter Category=Budget</c>.
/// <para>
/// A failing line here is a finding, not a budget to loosen. Every test prints a markdown row for
/// <c>docs/superpowers/plans/2026-09-20-phase1-spike-results.md</c> under "Phase 6 budget results".
/// </para></summary>
public class BudgetTests(ITestOutputHelper output)
{
    private const double ColdStartBudgetMs = 500;
    private const long PrivateBudgetBytes = 10 * 1024 * 1024;
    private const double IdleCpuBudgetMs = 50;     // the spec says 0; this is the measurement noise floor
    private const int HandleBudget = 100;
    private const int ThreadBudget = 5;
    private const double TickWallBudgetMs = 60;
    private const double TickCpuBudgetMs = 40;

    /// <summary>True when the run can go ahead; prints the reason and returns false when it cannot.</summary>
    private bool Ready()
    {
        if (Published.Skip is not { } why) return true;
        output.WriteLine(why);
        return false;
    }

    private void Row(string test, string measured, string budget, bool ok)
        => output.WriteLine($"| `{test}` | {measured} | {budget} | {(ok ? "OK" : "OVER")} |");

    [Fact]
    [Trait("Category", "Budget")]
    public void ColdStart_To_First_Wallpaper()
    {
        if (!Ready()) return;
        using var scratch = DaemonScratch.Create(Published.Exe!, "clock-disks.json");

        // Warm the base-image cache first, then remove the frame the warm-up wrote. Spec 1.2's cold
        // start is a cold PROCESS at sign-in, not a cold cache: the 3440x1440 raw dump of the base
        // photo is built once per display and survives reboots in the runtime dir. Deleting
        // deskwall.jpg also defeats the tick's skip gate, so the measured start must draw.
        scratch.Tick("--force", "--no-apply");
        File.Delete(scratch.OutPath);

        var daemon = scratch.StartDaemon();
        var ms = DaemonScratch.WaitForFile(scratch.OutPath, daemon, 30_000);
        Assert.True(ms is not null,
            $"no {scratch.OutPath} within 30 s" + (daemon.HasExited ? $"; the daemon exited with {daemon.ExitCode} (is another daemon already running?)" : ""));

        output.WriteLine($"cold start to first wallpaper: {ms!.Value:N0} ms (budget {ColdStartBudgetMs:N0} ms)");
        Row("ColdStart_To_First_Wallpaper", $"{ms.Value:N0} ms", "< 500 ms", ms.Value < ColdStartBudgetMs);
        Assert.True(ms.Value < ColdStartBudgetMs, $"cold start took {ms.Value:N0} ms, budget {ColdStartBudgetMs:N0} ms");
    }

    [Fact]
    [Trait("Category", "Budget")]
    public void Idle_PrivateBytes_After_Trim()
    {
        if (!Ready()) return;
        var s = IdleObservation.Value!;
        var mb = s.PrivateBytes / 1048576.0;
        output.WriteLine($"private bytes after 3 min idle: {mb:N2} MB (working set {s.WorkingSetBytes / 1048576.0:N2} MB)");
        Row("Idle_PrivateBytes_After_Trim", $"{mb:N2} MB", "< 10 MB", s.PrivateBytes < PrivateBudgetBytes);
        Assert.True(s.PrivateBytes < PrivateBudgetBytes, $"private bytes {mb:N2} MB, budget 10 MB");
    }

    [Fact]
    [Trait("Category", "Budget")]
    public void Idle_Cpu_Between_Wakes()
    {
        if (!Ready()) return;
        var s = IdleObservation.Value!;
        output.WriteLine($"cpu over {s.Window.TotalSeconds:N0} s: {s.WindowCpuMs:N0} ms total, " +
                         $"{s.TickCpuMs:N0} ms of it inside ticks, {s.IdleCpuMs:N0} ms between wakes");
        Row("Idle_Cpu_Between_Wakes", $"{s.IdleCpuMs:N0} ms", "< 50 ms", s.IdleCpuMs < IdleCpuBudgetMs);
        Assert.True(s.IdleCpuMs < IdleCpuBudgetMs,
            $"{s.IdleCpuMs:N0} ms of processor time between wakes over {s.Window.TotalSeconds:N0} s, budget {IdleCpuBudgetMs:N0} ms");
    }

    [Fact]
    [Trait("Category", "Budget")]
    public void Idle_Handles_And_Threads()
    {
        if (!Ready()) return;
        var s = IdleObservation.Value!;
        output.WriteLine($"after 3 min idle: {s.Handles} handles, {s.Threads} threads");
        Row("Idle_Handles_And_Threads", $"{s.Handles} h / {s.Threads} t", "< 100 h / < 5 t",
            s.Handles < HandleBudget && s.Threads < ThreadBudget);
        Assert.True(s.Handles < HandleBudget, $"{s.Handles} handles, budget {HandleBudget}");
        Assert.True(s.Threads < ThreadBudget, $"{s.Threads} threads, budget {ThreadBudget}");
    }

    [Fact]
    [Trait("Category", "Budget")]
    public void ClockOnly_Tick_Wall_And_Cpu()
    {
        if (!Ready()) return;
        using var scratch = DaemonScratch.Create(Published.Exe!, "clock-disks.json");

        // Two warm-up ticks: the first builds the base cache and frame.raw, the second proves the
        // skip gate is holding. Then forget only the clock's content key, which is exactly the state
        // the minute rolling over produces - one changed component down the incremental path. Waiting
        // for a real minute boundary would measure the same thing and sometimes straddle it.
        scratch.Tick("--force", "--no-apply");
        scratch.Tick("--no-apply");
        var state = FrameState.Load(scratch.StatePath);
        Assert.True(state.KeysById.Remove("clock"), "the clock-disks layout no longer has a component called 'clock'");
        state.Save(scratch.StatePath);

        var table = scratch.Tick("--measure", "--no-apply");
        output.WriteLine(table);
        var wall = Parse(table, "total");
        var cpu = ParseCpu(table);
        Assert.False(table.Contains("SKIPPED", StringComparison.Ordinal), $"the measured tick skipped, so it measured nothing:\n{table}");

        Row("ClockOnly_Tick_Wall_And_Cpu", $"{wall:N0} ms wall / {cpu:N0} ms cpu", "< 60 ms wall / < 40 ms cpu",
            wall < TickWallBudgetMs && cpu < TickCpuBudgetMs);
        Assert.True(wall < TickWallBudgetMs, $"tick wall {wall:N0} ms, budget {TickWallBudgetMs:N0} ms\n{table}");
        Assert.True(cpu < TickCpuBudgetMs, $"tick cpu {cpu:N0} ms, budget {TickCpuBudgetMs:N0} ms\n{table}");
    }

    /// <summary>One row of <see cref="TickTimings.ToTable"/>: "<c>&lt;stage&gt;  &lt;ms&gt;</c>".</summary>
    private static double Parse(string table, string stage)
    {
        var m = Regex.Match(table, $@"^{stage}\s+(-?[0-9,]+)", RegexOptions.Multiline);
        Assert.True(m.Success, $"no '{stage}' row in:\n{table}");
        return double.Parse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private static double ParseCpu(string table)
    {
        var m = Regex.Match(table, @"cpu\s+(-?[0-9,]+)");
        Assert.True(m.Success, $"no cpu figure in:\n{table}");
        return double.Parse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
    }
}
