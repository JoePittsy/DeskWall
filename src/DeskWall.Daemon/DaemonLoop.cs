using DeskWall.Core;
using DeskWall.Core.Diagnostics;
using DeskWall.Core.Display;
using DeskWall.Core.Events;
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

    /// <summary>The persisted provider records are rewritten no more often than this, however many
    /// events arrive. A slider drag is a coalesced wake every 400 ms and the file is only there so
    /// a provider survives a restart and the designer can see it; a 2 KB write per repaint would be
    /// paying for a promptness nobody asked for.</summary>
    private static readonly TimeSpan SaveEventsEvery = TimeSpan.FromSeconds(5);

    private readonly RemoteImageCache _images = RemoteImageCache.Default();
    private readonly HashSet<string> _seenProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _clashesLogged = new(StringComparer.OrdinalIgnoreCase);
    private EventBus? _bus;
    // Set on a pipe reader thread, read and cleared on the tick thread.
    private volatile bool _eventsDirty;
    private DateTimeOffset _eventsSaved;
    private Active? _active;
    private bool _paused;
    private DateTimeOffset _nextWake;
    private TickTimings? _last;
    private HostWindow? _win;
    private LayoutWatcher? _watcher;
    private string? _announced;   // the layout line last written to the log, so a reload does not repeat it

    public int Run()
    {
        // `run --no-tray` wins outright (tray is already false by then); otherwise the designer's
        // settings.json decides, read once here at start. It is never re-read: turning the tray icon
        // off or on is a restart, which is what the designer's settings page says it is.
        var wantTray = tray && DaemonSettings.TrayIconEnabled();

        using var win = new HostWindow();
        using var timer = new WaitableTimer();
        // Declared before the pipe so `using` disposes the pipe first: no line may reach a bus that
        // has already let go of its coalescing timer.
        using var bus = new EventBus(clock, EventBus.DefaultCoalesce);
        using var trayIcon = wantTray ? new TrayIcon(win) : null;
        using var watcher = new LayoutWatcher(store, () => win.Post(WakeKind.LayoutChanged));
        using var events = new EventPipeServer(line => Publish(bus, line), m => log.Warn($"events: {m}"));
        _win = win;
        _watcher = watcher;
        _bus = bus;
        var exit = false;

        // Spec section 3: every provider's last record is remembered, so a pushed widget survives a
        // sign-in and the designer can offer a binding for a producer it has never been told about.
        bus.Restore(EventStore.Load());
        // The one wake the seam adds, and it is one the daemon already knows: a late async source
        // and a landed image post exactly the same reason.
        bus.WakeRequested += () => _win?.Post(WakeKind.SourceCompleted);
        bus.ProviderChanged += _ => _eventsDirty = true;
        events.Start();

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
            win.TaskbarCreated += () => log.Info($"taskbar re-created; tray icon re-added {trayIcon.Added}");
        }

        log.Info($"daemon start pid {Environment.ProcessId} tray {trayIcon is not null}" +
                 (tray && !wantTray ? " (settings.json: trayIcon false)" : ""));
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
        // Stop listening before the last save, not after: an event accepted between the two
        // would be in the bus and not in the file, which is the one case where a producer sent
        // something and it was genuinely lost.
        events.Dispose();
        SaveEvents(force: true);   // whatever arrived since the last tick, before the process goes
        SourceFactory.DisposeAll(_active?.Sources);   // stop the samplers before the process goes
        _active = null;
        _win = null;
        _watcher = null;
        _bus = null;
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
            {
                // Every layout edit and every display change rebuilds the whole set of sources, and a
                // source may hold a timer and a native library (`hardware` holds both), so the set
                // being replaced has to be let go of. Disposed only once Activate has returned: if it
                // throws, _active still points at the old set and that set must still be alive.
                var replaced = _active;
                _active = Activate(monitor);
                if (!ReferenceEquals(replaced, _active)) SourceFactory.DisposeAll(replaced?.Sources);
            }
            if (_active is null)
            {
                // Spec 3.2: no layout means no frame. Whatever is on screen stays there.
                _nextWake = clock.Now.AddMinutes(1);
                return;
            }
            // Tick thread only. SourceRegistry is not synchronised and the tick reads it while it
            // resolves, so the bus (which is synchronised) is the only thing the pipe thread ever
            // touches, and this is where its state crosses over.
            SyncProviders(clock.Now);
            _last = _active.Runner.RunAsync(force, apply: true, CancellationToken.None).GetAwaiter().GetResult();
            log.Info($"tick {why}: {(_last.Skipped ? "skipped" : $"redrawn {_last.Redrawn}")} total {_last.TotalMs} ms cpu {_last.CpuMs:N0} ms");
            if (_active.Runner.LastShortcutOutcome is { } sc)
            {
                log.Info($"shortcuts: placed {sc.Positioned} / removed {sc.Removed} (written {sc.Written}) in {_last.ShortcutsMs} ms");
                foreach (var w in sc.Warnings) log.Warn($"shortcuts: {w}");
            }
            _nextWake = _active.Scheduler.NextWake(clock.Now);
            SaveEvents(force: false);
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
            Footprint.Release();   // collect, decommit, trim: the idle footprint is what the process costs between wakes
        }
    }

    /// <summary>Copy the bus's provider records into the active registry as values. Called on the
    /// tick thread immediately before the resolve, so a component bound to build.data.status sees
    /// what the pipe accepted since the last tick and never a half-applied event: Providers hands
    /// back a snapshot and each record inside it was swapped whole.</summary>
    private void SyncProviders(DateTimeOffset now)
    {
        if (_active is null || _bus is null) return;
        var providers = _bus.Providers;
        // A provider the designer forgot must stop being published rather than linger in this
        // registry for the life of the process.
        foreach (var gone in _active.Registry.ProviderNames.Where(n => !providers.ContainsKey(n)).ToList())
            _active.Registry.RemoveProvider(gone);
        foreach (var (name, record) in providers)
        {
            _active.Registry.SetProvider(name, record.ToValues(now));
            // Spec section 3: a layout source and a provider of the same name are not merged, the
            // layout source wins, and the clash is reported rather than left to the user as "my
            // events do nothing". Once per name: this runs on every tick.
            if (_active.Sources.Any(src => string.Equals(src.Name, name, StringComparison.OrdinalIgnoreCase)) && _clashesLogged.Add(name))
                log.Warn($"event provider '{name}' is shadowed by a layout source of the same name; the layout source wins");
        }
    }

    /// <summary>Write events.json, at most every few seconds, and unconditionally on shutdown. Tick
    /// thread only, so two writers can never race for the file.</summary>
    private void SaveEvents(bool force)
    {
        if (_bus is null || !_eventsDirty) return;
        var now = clock.Now;
        if (!force && now - _eventsSaved < SaveEventsEvery) return;
        try
        {
            // Cleared before the read, not after: an event that lands mid-save would otherwise be
            // marked saved when what went to disk predates it.
            _eventsDirty = false;
            EventStore.Save(_bus.Providers.Values);
            _eventsSaved = now;
        }
        catch (Exception ex)
        {
            // A provider that is not remembered costs a restart, not a frame; the tick goes on,
            // and the next one tries the write again.
            _eventsDirty = true;
            log.Warn($"events: could not save {EventStore.Path}: {ex.Message}");
        }
    }

    /// <summary>Hand one line to the bus and say in the log why it was turned down. Runs on a pipe
    /// reader thread: the bus and RollingLog are both thread safe, and the registry is
    /// deliberately not touched from here.</summary>
    private bool Publish(EventBus bus, string line)
    {
        if (bus.Publish(line))
        {
            // Once per provider name per run. "My script sends events and nothing happens" is
            // answered by whether this line is in the log, and a burst must not write a burst.
            string? source = null;
            lock (_seenProviders)
            {
                var last = bus.Recent.LastOrDefault(e => e.Accepted && e.Source is not null);
                if (last?.Source is { } name && _seenProviders.Add(name)) source = name;
            }
            if (source is not null) log.Info($"events: provider '{source}' seen");
            return true;
        }
        var why = bus.Recent.LastOrDefault(e => !e.Accepted && string.Equals(e.Line, line, StringComparison.Ordinal))?.Reason;
        log.Info($"events: rejected ({why ?? "unknown"}): {Trim(line)}");
        return false;
    }

    /// <summary>A rejected line belongs in the log, but a producer sending a megabyte of malformed
    /// JSON must not roll the log away.</summary>
    private static string Trim(string line) => line.Length <= 200 ? line : string.Concat(line.AsSpan(0, 200), "...");

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

/// <summary>Phase 5 ships DeskWall.Designer.exe next to the daemon, which is where an installed
/// DeskWall always finds it. In a dev tree the two projects build into their own bin folders, so the
/// tray item would otherwise never work on the machine it is being written on; the walk up to
/// src/DeskWall.Designer is a development convenience only and never fires for an installed copy,
/// which hits the first branch.</summary>
internal static class Designer
{
    public static void Open(RollingLog log)
    {
        var exe = Find();
        if (exe is null) { log.Warn("designer not installed"); return; }
        // One line per launch, so "it opened twice" is answerable from the log: two lines a few
        // milliseconds apart means the shell reported one click as two events, one line means it
        // did not and the second window came from somewhere else.
        log.Info("designer requested");
        // UseShellExecute false, and no redirection: the designer outlives this daemon happily and
        // inherits nothing it could block on.
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }); }
        catch (Exception ex) { log.Error("designer failed to start", ex); }
    }

    private static string? Find()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "DeskWall.Designer.exe");
        if (File.Exists(beside)) return beside;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var proj = Path.Combine(dir.FullName, "src", "DeskWall.Designer");
            if (Directory.Exists(proj))
            {
                var found = Directory.EnumerateFiles(proj, "DeskWall.Designer.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found is not null) return found;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
