using DeskWall.Core;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Scheduling;
using DeskWall.Core.Shortcuts;
using DeskWall.Core.Sources;
using DeskWall.Core.Tick;
using DeskWall.Core.Wallpaper;
using DeskWall.Daemon.Host;

namespace DeskWall.Daemon;

/// <summary>The resident process, spec 3.1. One hidden window, one waitable timer, one tray icon,
/// and a blocking wait in between ticks - no polling, no background thread, nothing running while
/// the machine is idle. Everything that can wake it (the timer, a display change, an unlock, a
/// layout file edit, a tray command, a late source) arrives as a window message; TickPlan decides
/// what a batch of those means and this class carries the decision out.</summary>
public sealed class DaemonLoop(RollingLog log, LayoutStore store, IClock clock, bool tray)
{
    /// <summary>Spec 3.2: a source that has missed this many of its own schedules stops publishing
    /// its last good values, so a bound component falls back instead of showing a frozen number.</summary>
    private const int StaleAfter = 3;

    /// <summary>False under `deskwall run --no-shortcuts`: the wallpaper still updates, but no desktop
    /// .lnk is written, moved or deleted. An init property rather than a constructor parameter so the
    /// constructor keeps its shape.</summary>
    public bool Shortcuts { get; init; } = true;

    private sealed record Active(
        MonitorInfo Monitor,
        LayoutResolution Resolution,
        List<ISource> Sources,
        SourceRegistry Registry,
        Scheduler Scheduler,
        TickRunner Runner,
        string SignatureKey);

    private readonly RemoteImageCache _images = RemoteImageCache.Default();
    private Active? _active;
    private bool _paused;
    private DateTimeOffset _nextWake;
    private TickTimings? _last;
    private HostWindow? _win;
    private LayoutWatcher? _watcher;
    private string? _announced;   // the layout line last written to the log, so a reload does not repeat it

    public int Run()
    {
        using var win = new HostWindow();
        using var timer = new WaitableTimer();
        using var trayIcon = tray ? new TrayIcon(win) : null;
        using var watcher = new LayoutWatcher(store, () => win.Post(WakeKind.LayoutChanged));
        _win = win;
        _watcher = watcher;
        var exit = false;

        if (trayIcon is not null)
        {
            trayIcon.Command += cmd =>
            {
                switch (cmd)
                {
                    case TrayCommand.RefreshNow:
                        win.Post(WakeKind.Manual);
                        break;
                    case TrayCommand.TogglePause:
                        _paused = !_paused;
                        trayIcon.Paused = _paused;
                        log.Info(_paused ? "paused" : "resumed");
                        win.Post(WakeKind.Manual);
                        break;
                    case TrayCommand.Exit:
                        exit = true;
                        win.Post(WakeKind.Shutdown);
                        break;
                    case TrayCommand.OpenDesigner:
                        Designer.Open(log);
                        break;
                }
            };
            if (!trayIcon.Added) log.Warn("tray icon could not be added");
        }

        log.Info($"daemon start pid {Environment.ProcessId} tray {trayIcon is not null}");
        WallpaperSetter.RecordRestorePoint();
        // A download that lands after the frame was drawn must repaint it; images nobody has looked
        // up for 30 days go now, once, not on a timer.
        _images.Landed += _ => win.Post(WakeKind.SourceCompleted);
        var swept = _images.Sweep();
        if (swept > 0) log.Info($"image cache swept {swept} file(s)");

        Tick("start", force: true, reactivate: false);
        trayIcon?.SetTooltip(Tooltip());

        while (!exit)
        {
            timer.SetDue(_nextWake);
            var reasons = win.WaitAndPump(timer);
            var plan = TickPlan.From(reasons);
            if (plan.Shutdown) break;
            if (!plan.Tick) continue;   // an unrelated window message woke the pump; back to sleep
            // Spec 3.1: Explorer is still re-laying the desktop right after a mode change, and the
            // shell hands back the old metrics until it finishes.
            if (plan.DelayForExplorer) Thread.Sleep(2000);
            Tick(Describe(reasons), plan.Force, plan.Reactivate);
            trayIcon?.SetTooltip(Tooltip());
        }

        log.Info("daemon stop");
        _win = null;
        _watcher = null;
        return 0;
    }

    private static string Describe(IReadOnlyList<WakeReason> reasons) => string.Join("+", reasons.Select(r => r.ToString()));

    private void Tick(string why, bool force, bool reactivate)
    {
        try
        {
            if (_paused)
            {
                _nextWake = clock.Now + Scheduler.MaxDelay;
                return;
            }
            var monitor = Monitors.Enumerate().FirstOrDefault(m => m.IsPrimary);
            if (monitor is null)
            {
                log.Warn("no primary monitor");
                _nextWake = clock.Now.AddMinutes(1);
                return;
            }
            if (_active is null || _active.SignatureKey != monitor.Signature.Key || reactivate)
                _active = Activate(monitor);
            if (_active is null)
            {
                // Spec 3.2: no layout means no frame. Whatever is on screen stays there.
                _nextWake = clock.Now.AddMinutes(1);
                return;
            }
            _last = _active.Runner.RunAsync(force, apply: true, CancellationToken.None).GetAwaiter().GetResult();
            log.Info($"tick {why}: {(_last.Skipped ? "skipped" : $"redrawn {_last.Redrawn}")} total {_last.TotalMs} ms cpu {_last.CpuMs:N0} ms");
            if (_active.Runner.LastShortcutOutcome is { } sc)
            {
                log.Info($"shortcuts: placed {sc.Positioned} / removed {sc.Removed} (written {sc.Written}) in {_last.ShortcutsMs} ms");
                foreach (var w in sc.Warnings) log.Warn($"shortcuts: {w}");
            }
            _nextWake = _active.Scheduler.NextWake(clock.Now);
        }
        catch (Exception ex)
        {
            // Spec 3.2: a failed tick changes nothing on screen; the previous wallpaper stays and the
            // next wake tries again. The one thing it must never do is take the daemon down.
            log.Error($"tick {why} failed", ex);
            _nextWake = clock.Now.AddMinutes(1);
        }
        finally
        {
            Footprint.Trim();
        }
    }

    private Active? Activate(MonitorInfo monitor)
    {
        store.Reload();      // layouts.json may have been written by a second process (deskwall layouts set)
        _watcher?.Rescan();  // and it may now name a layout in a directory nobody was watching
        var res = store.Resolve(monitor.Signature);
        if (res is null)
        {
            _announced = null;
            log.Warn($"no layout for {monitor.Signature.Key}; waiting (deskwall layouts set <path>)");
            return null;
        }
        // Every layout edit reactivates, so say which layout is in force only when the answer changes.
        var announce = res.Scaled
            ? $"scaled layout {res.SourcePath} from {res.SourceSignature.Key} to {monitor.Signature.Key}"
            : $"layout {res.SourcePath} for {monitor.Signature.Key}";
        if (announce != _announced) { log.Info(announce); _announced = announce; }

        var registry = new SourceRegistry { StaleAfter = StaleAfter };
        registry.StaleChanged += (name, stale) =>
        {
            if (stale) log.Warn($"source '{name}' is stale (missed {StaleAfter} refreshes); its values are no longer published");
            else log.Info($"source '{name}' is fresh again");
        };
        var sources = res.Layout.Sources.Select(s => SourceFactory.Create(s, clock)).ToList();
        foreach (var s in sources.OfType<AsyncSource>())
            s.Completed += _ => _win?.Post(WakeKind.SourceCompleted);   // a fetch that overran its timeout has landed

        var runner = new TickRunner(res.Layout, sources, registry, clock, monitor, images: _images, shortcuts: Shortcuts ? new ShortcutManager(Calibration.Load()) : null);
        return new Active(monitor, res, sources, registry, new Scheduler(sources, registry), runner, monitor.Signature.Key);
    }

    private string Tooltip()
    {
        var f = Footprint.Current();
        var next = _nextWake.ToLocalTime().ToString("HH:mm");
        var last = _last is null ? "" : $" . tick {_last.CpuMs:0} ms CPU";
        var err = log.LastError is null ? "" : " . ERROR see log";
        var state = _paused ? "Paused" : $"Next {next}";
        return $"DeskWall: {state}{last} . {f.WorkingSetBytes / 1048576.0:0.0} MB . GPU not used{err}";
    }
}

/// <summary>Phase 5 ships DeskWall.Designer.exe next to the daemon. Until then the tray's first menu
/// item says so in the log rather than appearing to do nothing.</summary>
internal static class Designer
{
    public static void Open(RollingLog log)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "DeskWall.Designer.exe");
        if (!File.Exists(exe)) { log.Warn("designer not installed"); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }); }
        catch (Exception ex) { log.Error("designer failed to start", ex); }
    }
}
