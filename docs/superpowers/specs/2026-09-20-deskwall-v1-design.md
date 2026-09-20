# DeskWall v1 design

Date: 2026-09-20. Status: approved in conversation, awaiting written review.
Supersedes the PowerShell proof of concept in the repo root (kept under `poc/` until parity).

## 1. Purpose and constraints

DeskWall paints slow-changing state into a static wallpaper image and places transparent
desktop shortcuts over parts of it so they launch on click. It is a cheap alternative to
Rainmeter and Wallpaper Engine: those keep a window and a GPU surface alive; DeskWall keeps a
JPEG.

Job statement (unchanged from the POC): answer "is a drive about to fill" and "what was I
playing" in one glance, on a surface that spends most of its life behind windows. The design
doctrine gates apply to every rendered layout and to the designer UI.

Audience: the owner first, built so it can be released. Portable, no admin, no assumptions
about which game stores or tools are installed, multi-monitor and DPI handled in the model.
Deferred to a later version: installer, auto-update, public plugin API, scripting host.

### 1.1 Non-negotiables

- **Tiny.** If a user notices the daemon in Task Manager, we have failed. Budgets in 1.2.
- **Measured, not eyeballed.** Placement, padding and cost are verified by pixel diff and by
  printed timings. `deskwall verify` and `deskwall tick --measure` are first-class commands.
- **No GPU.** Rendering is software Direct2D onto a WIC bitmap. No hardware D3D device is ever
  created, so no vendor GPU driver DLL is loaded into the process. (Direct2D's software path
  runs on WARP, Microsoft's in-process software rasteriser; that is the only D3D present.
  Measured 2026-09-20: see plans/2026-09-20-phase1-spike-results.md.)
- **Nothing runs unless it has to.** The designer is a separate process that exists only while
  open. Network fetches and command sources run on their own schedules with timeouts.

### 1.2 Cost budget

Asserted by budget tests on the reference machine (JOES-PC, i7-6700K, 3440x1440).

| Measure | Budget |
|---|---|
| Idle private working set (after trim) | 10 MB |
| Idle CPU between wakes | 0 |
| Clock-only tick, wall | 60 ms |
| Clock-only tick, CPU | 40 ms |
| Cold start to first wallpaper applied | 500 ms |
| Handles at idle | under 100 |
| Threads at idle | under 5 |

POC reference: about 370 ms wall and 190 ms CPU inside compose.ps1, plus an unmeasured
PowerShell spawn per minute, about 5 s cold start.

## 2. Technology

- **C# on .NET 10 (LTS).** One solution, three projects plus tests.
- **`DeskWall.Core`**: library, native-AOT safe, no UI. All logic and all tests.
- **`deskwall.exe`** (`DeskWall.Daemon`): native AOT, `WinExe` so no console window.
  Subcommands: `run` (default), `tick [--measure] [--force]`, `verify`, `calibrate`,
  `install`, `uninstall`.
- **`DeskWall.Designer.exe`**: WPF on the normal JIT runtime. Shares Core.
- **Graphics**: Direct2D, DirectWrite and WIC (Windows Imaging Component) through CsWin32
  compile-time interop. These DLLs are already resident in every Windows process.
- **Shell**: `IFolderView` / `IFolderView2` for icon placement and folder flags,
  `IDesktopWallpaper` for per-monitor wallpaper, `Shell_NotifyIcon` for the tray. CsWin32.
- **Serialization**: System.Text.Json with source generation.
- **Tests**: xUnit.
- **No third-party native dependencies.** LiteDB, Steam and Playnite code are not compiled in
  (see 4.3).

Spike before committing to the schedule: confirm CsWin32 covers `IFolderView2` and
`IDesktopWallpaper` cleanly under AOT, and measure a software Direct2D render plus WIC JPEG
encode of a 3440x1440 frame. Expected well under budget; if not, section 6 step 2 (skip when
unchanged) and per-rect encoding are the fallbacks.

## 3. Process model

### 3.1 Daemon lifecycle

1. Start. Create a hidden top-level window (needed to receive `WM_DISPLAYCHANGE`,
   `WM_SETTINGCHANGE`, `WM_WTSSESSION_CHANGE`, and tray messages). Register the tray icon if
   enabled. Load the layout store.
2. First tick: render, apply wallpaper, reconcile shortcuts.
3. Sleep: `MsgWaitForMultipleObjects` on a waitable timer set to the earliest due time across
   all sources, plus the message queue. No polling loop.
4. Wake on: timer, display change, session unlock, layout store file change, tray interaction.
   Every wake runs the same tick code path; display change adds a two-second delay before
   shortcut reconciliation so Explorer finishes its own re-layout.
5. After every tick: release all render objects, trim the working set, update the tray
   tooltip, compute next wake.

### 3.2 Error policy

- A tick never crashes the daemon. Exceptions are caught at the tick boundary, logged to a
  rolling file in the runtime dir, and the previous wallpaper stays.
- A failing source is marked failed with the error text, retried on its next schedule, and its
  bound components render their fallback. Staleness after which fallback applies is a
  component property (default: three missed schedules).
- A layout that fails to parse keeps the last good rendered image. The tray tooltip and the
  designer show the error.

### 3.3 Tray icon

On by default, hideable from the designer. Left click opens the designer. Menu: Open designer,
Refresh now, Pause, Exit. Tooltip (max 127 chars) refreshed after each tick and on hover:

    Next 14:32 . tick 38 ms CPU . 4.8 MB . GPU not used

Values come from `GetProcessTimes` and `GetProcessMemoryInfo`. "GPU not used" is literal,
see 1.1.

### 3.4 Install

`deskwall install` writes a `HKCU\...\Run` entry for the current user and starts the daemon.
`uninstall` stops it, removes the entry, deletes the slot shortcuts and ICO, restores the
desktop folder flags it changed, and restores the pre-DeskWall wallpaper recorded at first
run. No admin. No scheduled task.

## 4. Model: sources, bindings, components

### 4.1 Sources

A source runs on its own schedule and publishes typed values: scalars (text, number, time,
boolean), images (local path), and lists of records. Each source declares its schedule,
schema, availability on this machine, last-updated time, and last error.

| Source | Publishes | Schedule and notes |
|---|---|---|
| `time` | `now`, plus formatted variants | aligned to the minute boundary |
| `disks` | list of drives: `letter`, `free`, `total`, `usedFraction`, `label` | 5 min |
| `system` | `uptime`, `daysSinceCrash` (event 41/1001), `pendingReboot` | 15 min |
| `command` | `text`, or `json` when parseable; `exitCode`; `ranAt` | user schedule, hard timeout, hidden window, never on the tick thread |
| `http` | `text` or `json`; `status`; `fetchedAt` | user schedule, timeout, ETag/If-Modified-Since, optional headers, secrets by reference |
| `rss` | `title`; list `items`: `title`, `link`, `published`, `summary` | user schedule, RSS 2.0 and Atom |
| `file` | `text` or `json` from a local file | re-read on change (watcher) plus a slow schedule |

Rules:

- Network and command sources run off the tick thread and publish when done. The tick reads
  the last good result only.
- Remote images referenced by a binding are downloaded to a disk cache in the runtime dir,
  keyed by URL hash, revalidated on the owning source's schedule. The render path opens local
  files only.
- Secrets live in `secrets.json` in the runtime dir. Layouts reference them by name
  (`{secret:steamKey}`). A layout file never contains a secret.

### 4.2 Bindings

A component property is a literal or a binding: a path into a source's values, optional list
indexing, optional format string.

    time.now | "HH:mm"
    disks[C].free | "{0:N0} GB free"
    steam.json.response.games[0].appid | "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg"

Path grammar: identifiers separated by dots, `[n]` for list index, `[key]` for lookup by a
record's key field (drives by letter). Format is a .NET format string applied to the resolved
value. No arithmetic, no conditionals in v1. Threshold colouring on `bar` is a component
property, not a binding expression. Anything more is a `command` source.

### 4.3 Components

Each draws one thing into its rect from resolved properties.

| Component | Properties |
|---|---|
| `text` | `text`, `font`, `size`, `weight`, `color`, `align`, `style` (shadow blur radius, outline, or plate with opacity and radius) |
| `image` | `source` (path or URL), `fit` (cover, contain, stretch), `radius`, `opacity` |
| `bar` | `fraction`, `track`, `fill`, `threshold`, `thresholdFill`, `direction` |
| `shortcut` | `target` (program plus arguments, or URI), `tooltip`, `slot`; draws nothing, see 7 |
| `repeater` | `items` (a list binding), `axis`, `gap`, `template` (child components with rects relative to the item cell), `cellHeight` (`auto` keeps an image child's aspect ratio) |

Repeaters expand into concrete children at resolve time. Child shortcuts get slots
`base + index`.

Steam and Playnite ship as **starter layouts and recipes** under `layouts/`, not as code:
Steam via `http` against `IPlayerService/GetRecentlyPlayedGames` with covers by URL pattern
and `steam://rungameid/{appid}` targets; Playnite via `file` fed by Hearth.

### 4.4 Content keys and dirty tracking

Every expanded component's resolved properties hash to a content key. A component redraws only
when its key changes. If no key changes and the display signature is unchanged, the tick ends
before any drawing or encoding.

## 5. Layouts and display signatures

- **Layout file**: JSON, versioned. Contains: base image path and fit, JPEG quality or PNG,
  declared sources with settings and schedules, component instances with `id`, `type`, `rect`
  (physical pixels), `z`, properties and bindings.
- **Display signature**: monitor device path plus resolution plus scale, e.g.
  `DELL U3425WE @ 3440x1440 @ 100%`. The layout store (`layouts.json` in the runtime dir)
  maps signatures to layout files. Each monitor is looked up independently per tick.
- **Unknown signature**: pick the closest existing layout (same device path first, then same
  aspect ratio, then the most recently edited), scale it proportionally preserving image
  aspect ratios, apply it, log it, and let the designer offer to save it as the layout for the
  new signature. A store with no layouts at all renders the base image only.
- **Hot reload**: the daemon watches the store and layout files; a change triggers a forced
  tick.
- **Migrations**: a layout's `version` drives a chain of upgraders in Core, unit tested.

## 6. Rendering pipeline

Per tick:

1. **Resolve** bindings, expand repeaters, compute content keys.
2. **Skip** if nothing changed and the signature is unchanged.
3. **Base**: the base image scaled to the canvas, cached on disk as a raw bitmap keyed by
   path, size and fit, so loading is a copy not a decode.
4. **Draw**: software Direct2D render target over a WIC bitmap the size of the canvas. Copy
   the previous frame, then redraw in z-order every component whose key changed and every
   component whose rect intersects a changed rect. Text via DirectWrite. Default text style is
   a soft shadow with 6 px blur. Missing fonts fall back to Segoe UI with a designer warning.
5. **Encode**: WIC JPEG (default q92) or PNG to a temp file, atomic rename.
6. **Apply**: `IDesktopWallpaper::SetWallpaper` per monitor with the file path. The unchanged
   path still triggers a reload.
7. **Shortcuts**: if any shortcut's rect, target or tooltip changed, reconcile (section 7).
8. **Trim and sleep**.

`deskwall tick --measure` prints wall and CPU per stage. That output is the budget test's input.

## 7. Shortcut manager

Owns every desktop-icon concern; nothing else knows icons exist.

- Input: resolved shortcut components (rect, target, tooltip, slot). Effect: the desktop
  matches.
- One `.lnk` per slot on the user's desktop, named with `slot + 1` non-breaking spaces so no
  label renders. Icon is a transparent 256 px PNG-in-ICO generated once into the runtime dir.
  Slot N is always the same file; only target and tooltip change. Slots not in the current
  layout are deleted.
- Placement via `IFolderView::SelectAndPositionItems`, verified with `GetItemPosition`,
  retried up to three times with a short delay.
- **Arrow calibration**: the shortcut-arrow overlay rect (13x13 at (0, 40) for 48 px icons at
  100 %) is measured, not assumed. `deskwall calibrate` places one shortcut over a flat colour,
  screenshots, diffs, and stores the arrow rect per icon size and scale in the runtime dir.
  Placement puts the arrow at the layout's configured padding from the shortcut rect's
  bottom-left (default 5 px), matching the POC.
- **Preconditions enforced**: auto-arrange and snap-to-grid read via `IFolderView2` folder
  flags and turned off if on, previous values saved for uninstall. Hidden desktop icons mark
  the shortcut component unavailable with a designer explanation.
- Display change: wait about two seconds, then reconcile every slot against the new
  signature's layout.
- Not in v1: removing the arrow overlay globally (admin registry change). Documented as an
  optional manual step.

## 8. Designer

WPF. Runs only while open. Communicates with the daemon only through files.

- **Canvas**: scaled image of the selected monitor's canvas rendered by the same Core renderer
  as the daemon. Move by drag, resize by handles, arrow keys nudge 1 px (shift 10), snap to
  component and canvas edges with guides, zoom and pan, multi-select and align, undo/redo.
- **Left panel**: sources in this layout, add/configure, live values, last fetched, errors.
- **Right panel**: properties of the selection; each bindable property has a bind toggle
  opening a picker over the source value tree with a format box and live resolved preview.
- **Bottom panel**: z-ordered instance list.
- **Repeaters**: edit the template once; canvas shows all expanded instances, first one active.
- **Apply is save.** Also: revert to last applied.
- **Display selector** with "copy layout from" another signature, scaled.
- **Settings**: base image, encode quality, tray on/off, start at logon, run calibrate, run
  verify (result shown in-app), secrets editor, footprint panel (daemon working set, last tick
  timings per stage, uptime).
- **Starter layouts** offered on first run: the owner's right-hand column with Steam via
  `http`, clock-only, blank.
- Not in v1: expression editor beyond path plus format, themes, online sharing, marketplace.

## 9. Testing and verification

- **Unit (Core, xUnit)**: binding resolution and formatting; content-key stability; repeater
  expansion and slot assignment; layout parse and migration; signature matching and
  proportional scaling; RSS/Atom, JSON and command output parsing; staleness and fallback
  rules; scheduler next-wake computation with a fake clock; sources behind interfaces with
  fake fetchers.
- **Golden images**: fixed layout, fixed fake values, fonts shipped in test assets, rendered
  through the real Direct2D path, compared to checked-in PNGs with a per-pixel tolerance.
  The root `.gitignore` currently excludes `*.png`; it becomes scoped to runtime output so
  goldens under `tests/` are tracked.
- **Budget tests** (reference machine only, skipped in CI): every line of 1.2 via
  `tick --measure`, idle working set after ten minutes, zero CPU over a five-minute idle
  sample.
- **`deskwall verify`**: minimise windows, screenshot, diff against the composed image, report
  arrow padding per shortcut and a clock crop, exit non-zero on any mismatch.
- **Manual acceptance**: Apollo resolution change repairs itself; Explorer restart keeps
  icons; sleep and wake resumes on schedule; dead `http` endpoint leaves the old value;
  malformed layout keeps the old wallpaper; uninstall leaves the desktop as found.

## 10. Repository shape and build order

    src/DeskWall.Core/
    src/DeskWall.Daemon/
    src/DeskWall.Designer/
    tests/DeskWall.Core.Tests/
    layouts/                 starter layouts and recipes (steam via http, playnite via hearth)
    docs/                    this spec, layout format reference, source reference
    poc/                     the PowerShell POC, read-only until parity, then deleted

Build order, each step leaving something runnable:

1. Core model, `time` and `disks` sources, renderer, hand-written layout file, `tick`.
2. Daemon `run` with hidden window, scheduler, tray, hot reload, error policy, install.
3. Shortcut manager and `calibrate`.
4. `http`, `rss`, `file`, `command`, `system` sources; remote image cache; secrets.
5. Designer.
6. `verify`, golden and budget tests, starter layouts, docs.

Parity gate: the owner's current column reproduced as a starter layout, `deskwall verify`
passing, budgets met. Then the POC scheduled task is removed and `poc/` deleted in a later
commit.

## 11. Open questions carried forward

- Playnite's own last-played stays stale for Steam-launched sessions (Steam plugin 2.44).
  Irrelevant to the daemon now; relevant to the Hearth recipe.
- Whether Steam's public API rate limits are a problem at a 10-minute schedule (expected no).
- Whether to offer the global arrow-overlay removal as a guided admin step in a later version.
