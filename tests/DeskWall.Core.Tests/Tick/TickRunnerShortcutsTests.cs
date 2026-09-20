using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Shortcuts;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using Xunit;

namespace DeskWall.Core.Tests.Tick;

file sealed class ShortcutTickClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; set; } = now; }

/// <summary>Stage 6 of the tick. Nothing here goes near the real desktop's slot files: the manager is
/// pointed at a temp directory, and the one test that needs a desktop view returns early without one.</summary>
public class TickRunnerShortcutsTests
{
    private static (LayoutFile Layout, string Dir) Scene(string componentsJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "scut-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var basePng = Path.Combine(dir, "base.png");
        using (var b = Surface.Create(320, 180)) { b.Clear(new Color(255, 30, 30, 30)); b.SavePng(basePng); }
        var layout = LayoutFile.Parse($$"""
        { "version": 1, "baseImage": {{System.Text.Json.JsonSerializer.Serialize(basePng)}},
          "sources": [ { "name": "time", "type": "time" } ],
          "components": [ {{componentsJson}} ] }
        """);
        return (layout, dir);
    }

    private static TickRunner Runner(LayoutFile layout, string dir, IClock clock, ShortcutManager? manager)
    {
        var registry = new SourceRegistry();
        var sources = layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        var monitor = new MonitorInfo(new DisplaySignature("TEST", 320, 180, 100), new Rect(0, 0, 320, 180), true, "TEST");
        return new TickRunner(layout, sources, registry, clock, monitor,
            statePath: Path.Combine(dir, "state.json"), outPath: Path.Combine(dir, "out.jpg"),
            framePath: Path.Combine(dir, "frame.raw"), shortcuts: manager);
    }

    /// <summary>Two shortcut components in the same slot make Fingerprint throw. It used to throw
    /// outside stage 6's try, after the wallpaper was applied and before state.Save - so the state
    /// file never advanced and every tick threw, forever.</summary>
    [Fact]
    public async Task A_Duplicate_Slot_Is_A_Warning_Not_A_Thrown_Tick()
    {
        var (layout, dir) = Scene("""
            { "type": "shortcut", "id": "a", "rect": [0, 0, 40, 40], "slot": 60, "target": "explorer.exe" },
            { "type": "shortcut", "id": "b", "rect": [50, 0, 40, 40], "slot": 60, "target": "explorer.exe" }
            """);
        try
        {
            var clock = new ShortcutTickClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
            var runner = Runner(layout, dir, clock, new ShortcutManager(Calibration.Seed(), desktopDir: () => dir));

            var t = await runner.RunAsync(force: true, apply: false, default);

            Assert.True(File.Exists(Path.Combine(dir, "state.json")));   // the tick completed
            var outcome = Assert.IsType<ShortcutOutcome>(runner.LastShortcutOutcome);
            Assert.True(outcome.SlotFailed);
            Assert.Contains(outcome.Warnings, w => w.Contains("60", StringComparison.Ordinal));
            Assert.True(t.TotalMs >= 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A slot the manager could not place must be retried on the next tick even when that tick
    /// draws nothing - which is nearly every tick, since on the owner's layout only the clock moves and
    /// it moves once a minute.
    /// <para>
    /// This test used to be green against a broken runner. It asserted only that
    /// <c>LastShortcutOutcome</c> was non-null on the second run, and that field was cleared inside
    /// stage 6, which the skip gate returns before - so the skipped tick was still reporting the first
    /// tick's outcome. The clear now happens at the top of RunAsync, and the counter below is what makes
    /// the retry a fact rather than a leftover. It also no longer needs a real desktop: the manager is a
    /// fake, so the failure is stated instead of manufactured by locking a slot file.
    /// </para></summary>
    [Fact]
    public async Task A_Failed_Slot_Is_Retried_On_The_Next_Tick_Even_Though_The_Frame_Is_Skipped()
    {
        var (layout, dir) = Scene("""
            { "type": "shortcut", "id": "a", "rect": [0, 0, 40, 40], "slot": 60, "target": "explorer.exe" }
            """);
        try
        {
            var clock = new ShortcutTickClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
            var manager = new FakeShortcuts(dir) { Fail = true };
            var runner = Runner(layout, dir, clock, manager);
            var statePath = Path.Combine(dir, "state.json");

            await runner.RunAsync(force: true, apply: false, default);
            Assert.Equal(1, manager.Reconciles);
            Assert.True(runner.LastShortcutOutcome!.SlotFailed);
            var state = FrameState.Load(statePath);
            Assert.Equal("", state.ShortcutsFingerprint);
            Assert.True(state.ShortcutsRetryPending);

            // Nothing in the layout moves and the frame is already on disk, so this tick is skipped -
            // and the debt is still owed, so stage 6 runs anyway. This time the slot is placed.
            manager.Fail = false;
            clock.Now = clock.Now.AddMinutes(1);
            var skipped = await runner.RunAsync(force: false, apply: false, default);
            Assert.True(skipped.Skipped);
            Assert.Equal(2, manager.Reconciles);
            Assert.False(runner.LastShortcutOutcome!.SlotFailed);
            state = FrameState.Load(statePath);
            Assert.False(state.ShortcutsRetryPending);
            Assert.NotEqual("", state.ShortcutsFingerprint);

            // And now that nothing is owed, a skipped tick must cost no reconcile at all: this path is
            // 59 ticks in 60 and the whole reason stage 6 is fingerprint-gated.
            clock.Now = clock.Now.AddMinutes(1);
            var quiet = await runner.RunAsync(force: false, apply: false, default);
            Assert.True(quiet.Skipped);
            Assert.Equal(2, manager.Reconciles);
            Assert.Null(runner.LastShortcutOutcome);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The cheap path stays cheap: with no manager at all a skipped tick touches nothing, and
    /// the outcome is null rather than whatever the last reconciling tick left behind.</summary>
    [Fact]
    public async Task A_Skipped_Tick_With_No_Shortcuts_Manager_Reports_Nothing()
    {
        var (layout, dir) = Scene("""
            { "type": "shortcut", "id": "a", "rect": [0, 0, 40, 40], "slot": 60, "target": "explorer.exe" }
            """);
        try
        {
            var clock = new ShortcutTickClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
            var runner = Runner(layout, dir, clock, manager: null);
            await runner.RunAsync(force: true, apply: false, default);
            Assert.Null(runner.LastShortcutOutcome);

            clock.Now = clock.Now.AddMinutes(1);
            var t = await runner.RunAsync(force: false, apply: false, default);
            Assert.True(t.Skipped);
            Assert.Null(runner.LastShortcutOutcome);
            Assert.Equal(0, t.ShortcutsMs);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>A manager that reports an outcome instead of producing one. Fingerprint is inherited, not
/// overridden: it is pure by contract (it runs every tick and must not touch the shell), so leaving the
/// real one in place keeps the fingerprint gate under test rather than stubbed out.</summary>
file sealed class FakeShortcuts(string dir) : ShortcutManager(Calibration.Seed(), desktopDir: () => dir)
{
    public int Reconciles { get; private set; }

    public bool Fail { get; set; }

    public override ShortcutOutcome Reconcile(IReadOnlyList<ResolvedShortcut> shortcuts, int scalePercent)
    {
        Reconciles++;
        return Fail
            ? new ShortcutOutcome(0, 0, 0, ["slot 60: fake failure"]) { SlotFailed = true }
            : new ShortcutOutcome(1, 1, 0, []);
    }
}
