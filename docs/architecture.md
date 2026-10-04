# Architecture

Source of truth for this document: `src/DeskWall.Daemon/Program.cs`, `DaemonLoop.cs`,
`src/DeskWall.Core/Tick/TickRunner.cs`, `Paths.cs`, `Diagnostics/Footprint.cs`, and the spec
(`docs/design-spec.md`, sections 3 and 6). Every number cited
is from a measurement file; none is asserted here.

## The process model, in one page

Three things run, never more than two at once on an idle machine:

- **`deskwall.exe`** (`DeskWall.Daemon`) -- the only thing resident. `WinExe` (no console
  window), one hidden top-level window (class `DeskWallHost`) that exists solely to receive
  `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `WM_WTSSESSION_CHANGE` and tray messages, and one
  waitable timer. No polling loop and no background thread: between wakes the process is
  blocked in `MsgWaitForMultipleObjects` (`HostWindow.WaitAndPump`). One owner-approved
  exception (2026-09-21): a layout with a `hardware` source runs a thread-pool timer every 10 s
  that takes four native readings (`GetSystemTimes`, `GlobalMemoryStatusEx`, two NVML calls) into
  fixed ring buffers, so the dials can show five-minute averages; the daemon's own wake schedule
  is untouched and the wallpaper is still repainted only on the minute. Sources that hold a
  resource like that implement `IDisposable`, and every host (`DaemonLoop` on layout change and
  shutdown, the one-shot commands, the designer's live panel) disposes the set it replaces
  (`SourceFactory.DisposeAll`). Its measured cost is in the budget section. A second `deskwall run`
  while one is already running does not start a second daemon; it posts that daemon a "refresh
  now" message and exits (`Program.Run`, the `Local\DeskWall.Daemon` named mutex) -- one lock, and
  one window title, per runtime dir: `HostWindow.LockName` hashes a non-default `--home` into both,
  so `deskwall --home <scratch> stop` or `run` only ever reaches the daemon for that home; the
  default home keeps the title `DeskWallHost`, which is what older builds used too. The event
  pipe follows the same rule (`RuntimeInstance.EventPipeName`): `DeskWall.Events` for the default
  home, `DeskWall.Events.<hash>` for any other, printed by `deskwall [--home <dir>] pipe`.
- **`DeskWall.Designer.exe`** -- WPF, normal JIT runtime, exists only while the window is open.
  It never opens a channel to the daemon: it reads and writes the same files the daemon reads
  (the layout store, layout files, `secrets.json`, `settings.json`) and the daemon's hot-reload
  watcher picks up the change. The daemon's tray menu launches it as a plain child process
  (`Designer.Open` in `DaemonLoop.cs`) and otherwise knows nothing about it. One designer per
  runtime dir (`RuntimeInstance.DesignerLockName`, the same home hash as the daemon's lock). Above
  1:1 zoom the canvas re-renders the layout transformed into the viewport
  (`LayoutScaler.Transform`), never an upscaled bitmap, so a 13 px label stays crisp at 800%.
- **`DeskWall.Core`** -- a library, not a process. Both executables reference it; it contains
  every model, renderer and platform-interop type and is native-AOT-safe
  (`IsAotCompatible=true` in `Directory.Build.props`, enforced by the analyzer on every build,
  JIT or not).

`deskwall tick` (no `run`) is the fourth shape: one process, one tick, then exit -- the same code
path the resident daemon uses, useful for scripting and for this documentation's own examples.

## Daemon lifecycle

1. **Start.** Create the hidden host window, register the tray icon unless `--no-tray` (menu:
   Open designer, Refresh now, Pause, Exit), load the layout store, create the user widgets
   folder (`%LOCALAPPDATA%\DeskWall\widgets\`), watch the user widget file of every key the
   layout references (to reactivate when a widget is edited; the shipped folder is not watched,
   because it changes only on install and `publish.ps1` restarts the daemon then), route the
   remote image cache's `Landed` event to a wake, sweep image-cache entries untouched for 30 days.
2. **First tick**, forced (`Tick("start", force: true, ...)`), so the wallpaper is correct before
   the loop ever waits.
3. **Sleep.** The waitable timer is set to the earliest due time across every active source
   (`Scheduler.NextWake`, clamped to `[now + 250 ms, now + 15 min]`); the wait also wakes on any
   window message.
4. **Wake on:** the timer, a display change, a session unlock, an Explorer restart (the
   `TaskbarCreated` broadcast), a layout-store or layout-file
   change (`LayoutWatcher`, debounced), a widget file change (watched folder), a tray command, or
   a source finishing a fetch that overran into the next tick. `TickPlan.From(reasons)` turns the
   batch of reasons the pump collected into `{ Tick, Force, Reactivate, DelayForExplorer,
   Shutdown }`. A display change or an Explorer restart adds a two-second `Thread.Sleep` before
   the tick runs, because Explorer is still re-laying the desktop and hands back stale metrics
   until it finishes (spec 3.1); this blocks the pump, including a tray Exit, for those two
   seconds. The Explorer restart's tick is forced because a forced tick is the only one that
   re-places shortcuts whose planned positions have not changed (spec 9 "Explorer restart keeps
   icons"). A widget file change triggers a Reactivate.

   **Values arriving out of band take one path.** An async fetch landing late, a remote image
   landing, a watched file changing, an `audio`/`media`/`notifications` callback and a pipe event
   all end in `EventBus` (`ISignalSource.Changed` -> `EventBus.Signal(name)`, or `Publish` for the
   pipe), which raises one coalesced `WakeRequested` at most every 400 ms with a trailing wake, so
   the final state always lands; the daemon posts that as `WakeKind.SourceCompleted`. Signals are
   not written to the bus's diagnostics ring. The layout watcher, display change and Explorer
   restart stay separate: they are structural, not a value arriving. **A signal buys a wake and
   nothing else**: the tick that follows refreshes only what `Scheduler.IsDue` says is due, so a
   source that signals must also answer "due now" from `NextDue` (a pending flag, set where it
   raises `Changed`, cleared in `RefreshAsync`). Without it the first `audio` build woke the daemon
   for eight volume changes and a 6 s slider drag and repainted nothing; without clearing it, an
   always-due source pins the wake at `Scheduler.MinDelay`, four ticks a second. Time, disks,
   system, hardware, http, rss and non-streaming command stay on the schedule: they are
   schedule-shaped, and the pull path carries the failure back-off and staleness that the push
   path deliberately does not.
5. **After every tick:** log the outcome, update the tray tooltip, compute the next wake,
   `Footprint.Trim()` (`SetProcessWorkingSetSize`, giving freed pages back so Task Manager shows
   the idle number rather than the render peak).
6. **Stop.** `WM_CLOSE` (or the session ending, or tray Exit) is a Shutdown wake: the tick in
   flight finishes and the loop exits. `deskwall stop` posts `WM_CLOSE` to this runtime dir's host
   window and waits up to 10 s for the process to exit (exit 0 when it stopped or none was
   running, 1 on timeout); `deskwall uninstall` calls the same thing first.

A tick never takes the daemon down: exceptions are caught at the `DaemonLoop.Tick` boundary,
logged (`RollingLog.Error`), and the wallpaper already on screen stays (spec 3.2). The failure
path retries in one minute rather than computing a real backoff, since the failure is in the tick
itself, not a single source.

## The tick pipeline

`TickRunner.RunAsync(force, apply, ct)` -- one method, seven numbered stages (spec section 6),
timed into a `TickTimings` that `deskwall tick --measure` prints as a table, followed by a
per-layer draw table, dearest first (`TickRunner.MeasureLayers` turns on `FrameRenderer.LayerMs`;
the resident daemon leaves it off). Before the tick proper,
`LayoutStore.TryLoad` reads and expands the layout:

- **Load.** Read the layout file from disk, check its version, expand any linked widget copies
  (`WidgetExpander.Expand`) into ordinary sources and components, and scale the result to the
  current display signature if needed (`LayoutScaler.Scale`). This is measured as `load` in the
  timing output (read + expand + scale time, before the tick). The expander resolves widget files
  from the user folder first (`%LOCALAPPDATA%\DeskWall\widgets\`) and then the shipped folder
  beside the exe, enabling copy-on-write for a shipped widget: editing it writes a user copy and
  every placed copy of that widget, in every layout on the machine, follows the edit. Only the
  referenced keys are read. Measured on JOES-XPS-17 (AOT, `column-system.json`, 2026-09-29):
  `load` is 4-7 ms for the v1 file and 11-13 ms for its v2 form (15 widget files, 8 copies);
  `total`, `cpu`, the skip path and idle handles (+1-2) were unchanged within one 15.6 ms quantum
  over 30 interleaved samples per build. The daemon pays `load` once per activation (a layout or
  widget edit, a display change), never per minute; a one-shot `tick` pays it every time.

The tick itself:

1. **Refresh due sources.** For each source, `Scheduler.IsDue` (not the source's own `NextDue`
   directly -- the wake math and the run gate must agree exactly, or a source can be due by one
   test and not the other) decides whether to call `RefreshAsync`. Every due refresh is started
   before any is awaited, so two `command` sources that each spend a second starting
   `powershell.exe` cost one second of wall time, not two; results are still written to the
   registry in layout order. A source that throws is marked failed; its previous values keep
   publishing.
2. **Resolve and diff.** `LayoutResolver.Resolve` expands the layout (repeaters, after copies are
   already expanded) against the current value tree and computes each component's content key.
   This is compared against `frame-state.json`'s `KeysById` from the previous tick. If nothing
   changed, nothing was removed, the display signature matches, and the base image's cache key
   matches, the tick ends here (`t.Skipped = true`) before any drawing, encoding or applying --
   the whole point of content keys (spec 4.4).
3. **Base image.** The layout's `baseImage` is resolved against the value tree first (it may be
   bound, e.g. to `time.phase`; a resolved path that does not exist falls back to the last base
   drawn, `FrameState.BasePath`, with one warning). `BaseCache.Ensure` scales that photo to the
   canvas once and caches the result as a raw PBGRA dump under `runtime/base/<w>x<h>-<key>.raw`,
   keyed by the resolved path, mtime, target size and fit, so a cache hit is a file copy, not a
   JPEG/PNG decode. It keeps the four most recently used raws per canvas size, so a layout that
   swaps between four phase photos never re-decodes one.
4. **Draw.** A forced tick, or one where the display signature or base image changed, renders
   every component fresh (`FrameRenderer.RenderAll`). Otherwise only components whose content key
   changed, or whose paint bounds intersect one that did, are redrawn onto the previous frame
   loaded from `frame.raw` (`RenderIncremental`, which mutates that frame in place rather than
   allocating a second full-size buffer). A bar whose track, fill and glow are all fully
   transparent is skipped (`FrameRenderer`), as is an image at opacity 0 or with a fully
   transparent `tint`, so weather and warning
   overlays cost nothing while invisible. A pure upscale whose destination is at least 250,000 px
   draws with linear interpolation (`Surface.DrawSurface`): `HIGH_QUALITY_CUBIC`'s cost scales
   with destination area, and two 8x512 gradient strips stretched over the canvas measured 45-59
   and 31-42 ms with it, 3-5 ms without. Icon-sized upscales keep the cubic filter (the
   `image-fits` and `repeater-auto` goldens fail without it). Measured per layer on the 50-component
   `alpine-vision.json` (AOT, 3440x1440): no layer above 12 ms; 400 round-cap stars cost 5.8 ms
   over four layers and 600 rain strokes 5.5 ms, so caching path geometry is not needed.
5. **Encode.** The frame is written to a temp file and renamed into place (`Surface.SaveJpeg` /
   `SavePng`, and `SaveRaw` for `frame.raw` itself) -- an atomic replace, never a partial file on
   disk mid-write.
6. **Apply.** `IDesktopWallpaper::SetWallpaper` with the encoded file's path. Skipped entirely
   when the caller passed `apply: false` (`deskwall tick --no-apply`). Setting the *same* path
   again still reloads the image (measured, spike results); this is what lets the output file
   name stay constant tick after tick.
7. **Shortcuts.** If the shortcut manager's fingerprint (slots, rects, targets, tooltips, pad,
   the arrow rect for the current icon size/scale) differs from the last one recorded, reconcile
   the desktop (`ShortcutManager.Reconcile`, `docs/design-spec.md` section 7). Skipped
   when nothing shortcut-related changed, because talking to Explorer costs far more than the
   rest of a tick combined. A reconcile failure (a bad slot, Explorer briefly gone) leaves the
   fingerprint unstored so the next tick retries, rather than leaving a broken icon in place
   indefinitely.

State is then written (`frame-state.json`): content keys and paint bounds per component id (used
next tick to repaint the base under anything that disappeared), the display signature, the base
image's cache key, and the shortcuts fingerprint.

## Where files live

Everything DeskWall writes lives under `%LOCALAPPDATA%\DeskWall` (`Paths.RuntimeDir`), overridable
with the `DESKWALL_HOME` environment variable or `deskwall --home <dir>` (tests always set this,
so they never touch the real runtime dir). Gitignored; `deskwall paths` prints the resolved
directory.

| Path | Written by | Contents |
|---|---|---|
| `layouts.json` | `LayoutStore` | Display signature -> layout file path. |
| `secrets.json` | the owner / designer only | `{secret:name}` substitutions; never written by the daemon or `tick`. |
| `settings.json` | the designer | Tray on/off, start-at-logon, last-opened layout, panel layout. |
| `calibration.json` | `deskwall calibrate` | Arrow-overlay rect per `(icon size, display scale)`. |
| `desktop-flags.json` | `DesktopFlags` | The desktop's original auto-arrange and snap-to-grid flags, saved the first time placement turns them off (through `IFolderView2` folder flags, never Explorer's registry) and never overwritten after; `deskwall uninstall` restores them and deletes the file. |
| `events.json` | `EventBus` / `EventStore` | Every pushed provider's last record (`docs/sources.md` "Pushed values: events"), written atomically at most every few seconds and on shutdown; the designer watches it. |
| `widgets/<key>.json`, `providers/<name>.json` | the designer / the owner | User widget files (shadowing shipped ones by key) and provider manifests. |
| `shortcuts-owned.json` | `ShortcutManager` | Slot -> hash of the spec last written there; the only slots `ShortcutManager` will ever delete. |
| `frame-state.json` | `TickRunner` | Content keys, paint bounds, signature, base-image key, the last base photo drawn (and any missing one already warned about) and shortcuts fingerprint from the last tick -- the skip gate's input. |
| `frame.raw` | `TickRunner` / `Surface.SaveRaw` | The full previous frame as raw PBGRA, loaded (not decoded) for incremental redraw. Deliberately never held in memory across ticks: at 3440x1440 it is ~19.8 MB, which alone would blow the 10 MB idle budget. |
| `base/<w>x<h>-<key>.raw` | `BaseCache` | Base photos pre-scaled to the canvas. A tick that writes a new one keeps the four most recently used for that canvas size (a hit refreshes its mtime) and deletes the rest of that size; other sizes (the designer's preview renders here too) and old unprefixed names go once untouched for a day. |
| `images/<sha256-16>.img` + `.meta` | `RemoteImageCache` | Downloaded remote images and their revalidation metadata; entries unused for 30 days are swept once at daemon start. |
| `deskwall.jpg` / `deskwall.png` | `TickRunner` | The composed frame actually set as the wallpaper. |
| `restore.json` | `WallpaperSetter.RecordRestorePoint` | The pre-DeskWall wallpaper per monitor, written once; consumed and deleted by `deskwall uninstall`. |
| `blank.ico` | `BlankIcon.Ensure` | A fully transparent 256 px icon-in-ICO, generated once, used for every shortcut slot. |
| `deskwall.log` (+ `.1.log`) | `RollingLog` | Append-only, rolled at 1 MB; `RollingLog.LastError` is what puts "ERROR see log" in the tray tooltip. |
| `calibrate-log.txt` | `deskwall calibrate` | A plain-text tee of that command's console output, since `MinimizeAll` takes the console with it. |
| `verify-desktop.png`, `clock-now.png`, `verify-log.txt` | `deskwall verify` | The right-hand 400 px column of the screenshot, the clock at 8x, and a tee of the report (same reason as `calibrate-log.txt`). |

## The cost budget and how it is enforced

Spec 1.2's table (reproduced from `docs/design-spec.md`):

| Measure | Budget |
|---|---|
| Idle private working set (after trim) | 10 MB |
| Idle CPU between wakes | 0 |
| Clock-only tick, wall | 60 ms |
| Clock-only tick, CPU | 40 ms |
| Cold start to first wallpaper applied | 500 ms |
| Handles at idle | under 100 |
| Threads at idle | under 5 |

**This budget is asserted only under native AOT**, by budget tests
(`[Trait("Category", "Budget")]`, excluded from a default `dotnet test` run, run explicitly with
`dotnet test --filter Category=Budget` against a published `deskwall.exe`) driving `deskwall tick
--measure` and `Footprint.Current()` on the reference machine (JOES-PC, i7-6700K, 3440x1440).

**Native AOT runs, 2026-09-21** (`deskwall.exe` 6.54 MB, MSVC 14.51, SDK 10.0.26100; the full
rows and their caveats are in `docs/measurements.md` under
"Phase 6 budget results"). Three of the five budgets are met, two are not:

| Budget row | First run | After the memory fix wave | Verdict |
|---|---|---|---|
| Cold start to first wallpaper | 110 ms | 99 ms | OK |
| Idle CPU between wakes, 4 min | 0 ms outside ticks | 0 ms | OK |
| Idle private bytes after trim | 48.4 MB (working set 1.6 MB) | 8.2 MB (working set 0.6 MB) | OK |
| Idle handles / threads | 279 / 9 | 279 / 9 | OVER |
| Clock-only tick | 146 ms wall / 78 ms CPU | 92 ms wall / 62 ms CPU | OVER |

What the first run found, and what the fix wave did about it:

- **Private bytes was commit the GC never gave back.** After `Footprint.Trim()` the working set
  was 1.6 MB, but `LoadRaw` and `SaveRaw` each staged the whole 19.8 MB frame in a managed byte
  array, which lands on the large object heap and waits for a gen2 collection the idle daemon
  rarely ran; sampled from outside it swung 48-79 MB tick to tick. Now the frame streams straight
  between the file and the locked WIC bitmap, and after every tick `Footprint.Release()` runs an
  aggressive compacting gen2 collection before the trim. Idle commit went to 8.2 MB.
- **The clock-only tick was not cheaper than a full redraw.** In a one-shot process, forced full
  redraws (7 components) measured draw 88-109 ms and clock-only incremental ticks (1 component)
  105-117 ms; the two 19.8 MB managed copies were most of the difference. Removing them took the
  clock-only draw stage from 118 to 65 ms. What is left: 65 ms of draw for one component, which
  is still the `frame.raw` read and write plus factory and render-target setup in a fresh process,
  and a steady 24 ms of JPEG encode. The CPU figure moves in 15.6 ms quanta (62 is 4 quanta).
- **Handles and threads are the runtime plus the cached factories, not per-tick growth.** 257
  handles / 17 threads two seconds after start, before any tick; 261 / 10 after several ticks.
  The Direct2D, DirectWrite and WIC factories are process-lifetime singletons (`Surface`) and the
  render target is per frame. Nothing in the fix wave touched this and the numbers did not move.
  Attributed on 2026-09-30 (JOES-XPS-17, AOT, `clock-disks.json`, 10 minutes resident; spike
  results "Parity-gate pass"): of ~305 handles, ~120 are of a type that cannot be duplicated
  out of the process (consistent with the ETW registrations of the d3d11/dxgi/WARP/d2d1/DWrite
  stack) and 81 are events; of 7-13 threads, only 3 run DeskWall code (main, finalizer, the
  event-pipe server), one is COM's, and the rest are idle Windows thread-pool workers. Switching
  to .NET's portable thread pool (`DOTNET_ThreadPool_UseWindowsThreadPool=0`) measured the same.
  Meeting either row means not holding the graphics stack in the resident process (render in a
  short-lived child, or unload it between ticks), which is a change to spec 3.1's process model
  and so the owner's decision, not a tuning pass.

**The `column-system.json` layout, resident on JOES-PC, 2026-09-21** (AOT, `hardware` source
sampling every 10 s, weather and Tailscale recipes, four dials; sampled from outside every minute
for four minutes, CPU split by the daemon's own tick log):

| Measure | Build 80767b1, one tick a minute | Budget | Verdict |
|---|---|---|---|
| Private bytes, idle | 30.2-30.7 MB, flat | 10 MB | OVER |
| Working set after trim | 1.8-3.3 MB | (not a row) | |
| Handles / threads | 467 / 12 | 100 / 5 | OVER |
| CPU outside ticks, 240 s | 0 ms (24 sampler fires) | 50 ms | OK |
| Per-minute tick (8 components redrawn) | 89-94 ms wall / 78-94 ms CPU | 60 / 40 | OVER |

What the column costs over `clock-disks.json` on the same build: about 22 MB of commit, 190
handles and 3 threads, all of it NVML loaded in-process (the library initialises once per source
and stays resident so the GPU dial can be read every 10 s without a process spawn). The
sampler's own CPU is below one 15.6 ms quantum per four minutes. The per-minute tick is heavier
than the clock-only one because the four dials and their numbers change every minute: eight
components redrawn, not one. An earlier build of the same day (fd6a29b) scheduled the hardware
source from its first refresh rather than the minute boundary and repainted twice a minute
(:00 and :30); that measured 45.8 MB private bytes and 63 ms outside ticks, and was fixed the
same morning. These are the owner's accepted costs for the widgets he asked for; they are
recorded, not hidden, and the two OVER rows are the same two open findings as the clock-only
layout plus NVML's footprint.

Under JIT (phase 2, JOES-PC) the same daemon measured 15.5 MB working set, 69.8 MB private
bytes, 368 handles and 15 threads resident, with a 67-90 ms clock-only tick. Against the POC
reference in spec 1.2 (about 370 ms wall / 190 ms CPU per tick, ~5 s cold start) every row is
already a large improvement; against the spec's own table, three rows are open findings.

`deskwall verify` (README "Verify") is the pixel-diff half of "measured, not eyeballed": it
minimises windows, screenshots the primary monitor, diffs against the composed frame, and reports
per-shortcut arrow padding and a clock crop, independent of the timing numbers above. It is a
command, never on the tick path, and writes only `verify-desktop.png`, `clock-now.png` and
`verify-log.txt` into the runtime dir.
