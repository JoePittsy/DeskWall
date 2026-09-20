using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
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

    /// <summary>A slot the manager could not write must not have its fingerprint stored, or the icon
    /// stays missing until the game list or the layout happens to change. The next tick reconciles
    /// again even though nothing about the layout moved.</summary>
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task A_Failed_Slot_Leaves_The_Fingerprint_Unstored_So_The_Next_Tick_Retries()
    {
        if (!DesktopView.IsAvailable()) return;   // Session 0 or a locked workstation: skip, do not fail
        var (layout, dir) = Scene("""
            { "type": "shortcut", "id": "a", "rect": [0, 0, 40, 40], "slot": 60, "target": "explorer.exe" }
            """);
        var ownedFile = Paths.InRuntime("shortcuts-owned.json");
        if (File.Exists(ownedFile)) File.Delete(ownedFile);
        try
        {
            var clock = new ShortcutTickClock(new DateTimeOffset(2026, 9, 20, 14, 32, 5, TimeSpan.Zero));
            var runner = Runner(layout, dir, clock, new ShortcutManager(Calibration.Seed(), desktopDir: () => dir));
            var slotPath = Path.Combine(dir, ShortcutPlan.SlotFileName(60));
            using (new FileStream(slotPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await runner.RunAsync(force: true, apply: false, default);
                Assert.True(runner.LastShortcutOutcome!.SlotFailed);
                Assert.Equal("", FrameState.Load(Path.Combine(dir, "state.json")).ShortcutsFingerprint);

                // Same layout, same clock minute: without the fix the fingerprint matched and stage 6
                // did nothing at all, so LastShortcutOutcome would be null here.
                clock.Now = clock.Now.AddMinutes(1);
                await runner.RunAsync(force: false, apply: false, default);
                Assert.NotNull(runner.LastShortcutOutcome);
                Assert.True(runner.LastShortcutOutcome!.SlotFailed);
            }
        }
        finally
        {
            DesktopFlags.Restore();
            Directory.Delete(dir, recursive: true);
            if (File.Exists(ownedFile)) File.Delete(ownedFile);
        }
    }
}
