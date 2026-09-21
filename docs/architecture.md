# Architecture

Source of truth for this document: `src/DeskWall.Daemon/Program.cs`, `DaemonLoop.cs`,
`src/DeskWall.Core/Tick/TickRunner.cs`, `Paths.cs`, `Diagnostics/Footprint.cs`, and the spec
(`docs/superpowers/specs/2026-09-20-deskwall-v1-design.md`, sections 3 and 6). Every number cited
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
  now" message and exits (`Program.Run`, the `Local\DeskWall.Daemon` named mutex).
- **`DeskWall.Designer.exe`** -- WPF, normal JIT runtime, exists only while the window is open.
  It never opens a channel to the daemon: it reads and writes the same files the daemon reads
  (the layout store, layout files, `secrets.json`, `settings.json`) and the daemon's hot-reload
  watcher picks up the change. The daemon's tray menu launches it as a plain child process
  (`Designer.Open` in `DaemonLoop.cs`) and otherwise knows nothing about it.
- **`DeskWall.Core`** -- a library, not a process. Both executables reference it; it contains
  every model, renderer and platform-interop type and is native-AOT-safe
  (`IsAotCompatible=true` in `Directory.Build.props`, enforced by the analyzer on every build,
  JIT or not).

`deskwall tick` (no `run`) is the fourth shape: one process, one tick, then exit -- the same code
path the resident daemon uses, useful for scripting and for this documentation's own examples.

## Daemon lifecycle

1. **Start.** Create the hidden host window, register the tray icon unless `--no-tray` (menu:
   Open designer, Refresh now, Pause, Exit), load the layout store, subscribe the remote image
   cache's `Landed` event to a wake, sweep image-cache entries untouched for 30 days.
2. **First tick**, forced (`Tick("start", force: true, ...)`), so the wallpaper is correct before
   the loop ever waits.
3. **Sleep.** The waitable timer is set to the earliest due time across every active source
   (`Scheduler.NextWake`, clamped to `[now + 250 ms, now + 15 min]`); the wait also wakes on any
   window message.
4. **Wake on:** the timer, a display change, a session unlock, a layout-store or layout-file
   change (`LayoutWatcher`, debounced), a tray command, or a source finishing a fetch that
   overran into the next tick. `TickPlan.From(reasons)` turns the batch of reasons the pump
   collected into `{ Tick, Force, Reactivate, DelayForExplorer, Shutdown }`. A display change
   adds a two-second `Thread.Sleep` before the tick runs, because Explorer is still re-laying the
   desktop and hands back stale metrics until it finishes (spec 3.1); this blocks the pump,
   including a tray Exit, for those two seconds.
5. **After every tick:** log the outcome, update the tray tooltip, compute the next wake,
   `Footprint.Trim()` (`SetProcessWorkingSetSize`, giving freed pages back so Task Manager shows
   the idle number rather than the render peak).

A tick never takes the daemon down: exceptions are caught at the `DaemonLoop.Tick` boundary,
logged (`RollingLog.Error`), and the wallpaper already on screen stays (spec 3.2). The failure
path retries in one minute rather than computing a real backoff, since the failure is in the tick
itself, not a single source.

## The tick pipeline

`TickRunner.RunAsync(force, apply, ct)` -- one method, seven numbered stages (spec section 6),
timed into a `TickTimings` that `deskwall tick --measure` prints as a table:

1. **Refresh due sources.** For each source, `Scheduler.IsDue` (not the source's own `NextDue`
   directly -- the wake math and the run gate must agree exactly, or a source can be due by one
   test and not the other) decides whether to call `RefreshAsync`. A source that throws is marked
   failed; its previous values keep publishing.
2. **Resolve and diff.** `LayoutResolver.Resolve` expands the layout (repeaters included) against
   the current value tree and computes each component's content key. This is compared against
   `frame-state.json`'s `KeysById` from the previous tick. If nothing changed, nothing was
   removed, the display signature matches, and the base image's cache key matches, the tick ends
   here (`t.Skipped = true`) before any drawing, encoding or applying -- the whole point of
   content keys (spec 4.4).
3. **Base image.** `BaseCache.Ensure` scales the base image to the canvas once and caches the
   result as a raw PBGRA dump under `runtime/base/<key>.raw`, keyed by path, mtime, target size
   and fit, so a cache hit is a file copy, not a JPEG/PNG decode.
4. **Draw.** A forced tick, or one where the display signature or base image changed, renders
   every component fresh (`FrameRenderer.RenderAll`). Otherwise only components whose content key
   changed, or whose paint bounds intersect one that did, are redrawn onto the previous frame
   loaded from `frame.raw` (`RenderIncremental`, which mutates that frame in place rather than
   allocating a second full-size buffer).
5. **Encode.** The frame is written to a temp file and renamed into place (`Surface.SaveJpeg` /
   `SavePng`, and `SaveRaw` for `frame.raw` itself) -- an atomic replace, never a partial file on
   disk mid-write.
6. **Apply.** `IDesktopWallpaper::SetWallpaper` with the encoded file's path. Skipped entirely
   when the caller passed `apply: false` (`deskwall tick --no-apply`). Setting the *same* path
   again still reloads the image (measured, spike results); this is what lets the output file
   name stay constant tick after tick.
7. **Shortcuts.** If the shortcut manager's fingerprint (slots, rects, targets, tooltips, pad,
   the arrow rect for the current icon size/scale) differs from the last one recorded, reconcile
   the desktop (`ShortcutManager.Reconcile`, `docs/superpowers/specs/...` section 7). Skipped
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
| `shortcuts-owned.json` | `ShortcutManager` | Slot -> hash of the spec last written there; the only slots `ShortcutManager` will ever delete. |
| `frame-state.json` | `TickRunner` | Content keys, paint bounds, signature, base-image key and shortcuts fingerprint from the last tick -- the skip gate's input. |
| `frame.raw` | `TickRunner` / `Surface.SaveRaw` | The full previous frame as raw PBGRA, loaded (not decoded) for incremental redraw. Deliberately never held in memory across ticks: at 3440x1440 it is ~19.8 MB, which alone would blow the 10 MB idle budget. |
| `base/<key>.raw` | `BaseCache` | The base image pre-scaled to the canvas; entries untouched for a day are swept on the next tick that writes a new one. |
| `images/<sha256-16>.img` + `.meta` | `RemoteImageCache` | Downloaded remote images and their revalidation metadata; entries unused for 30 days are swept once at daemon start. |
| `deskwall.jpg` / `deskwall.png` | `TickRunner` | The composed frame actually set as the wallpaper. |
| `restore.json` | `WallpaperSetter.RecordRestorePoint` | The pre-DeskWall wallpaper per monitor, written once; consumed and deleted by `deskwall uninstall`. |
| `blank.ico` | `BlankIcon.Ensure` | A fully transparent 256 px icon-in-ICO, generated once, used for every shortcut slot. |
| `deskwall.log` (+ `.1.log`) | `RollingLog` | Append-only, rolled at 1 MB; `RollingLog.LastError` is what puts "ERROR see log" in the tray tooltip. |
| `calibrate-log.txt` | `deskwall calibrate` | A plain-text tee of that command's console output, since `MinimizeAll` takes the console with it. |

## The cost budget and how it is enforced

Spec 1.2's table (reproduced from `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md`):

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
rows and their caveats are in `docs/superpowers/plans/2026-09-20-phase1-spike-results.md` under
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

The JIT numbers this section used to carry (15.5 MB working set, 69.8 MB private bytes, 368
handles, 15 threads resident; 67-90 ms clock-only tick) are in
`.superpowers/sdd/2026-09-20-deskwall-v1-phase2-daemon/lane-loop-report.md`. Against the POC
reference in spec 1.2 (about 370 ms wall / 190 ms CPU per tick, ~5 s cold start) every row is
already a large improvement; against the spec's own table, three rows are open findings.

`deskwall verify` (in progress in another lane at the time of writing -- see the phase 6 plan's
Task 1) is the pixel-diff half of "measured, not eyeballed": it minimises windows, screenshots
the primary monitor, diffs against the composed frame, and reports per-shortcut arrow padding and
a clock crop, independent of the timing numbers above.
