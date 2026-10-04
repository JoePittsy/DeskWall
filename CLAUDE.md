# DeskWall — agent notes

Read this before touching anything. README.md is the human-facing overview; this file is the
stuff you would otherwise rediscover the hard way.

## What this is, in one sentence

A tool that paints slow-changing state (clock, recently played games, disk headroom) into a
static wallpaper image every minute, with transparent desktop shortcuts placed over the game
covers so they launch on click. Owner: Joe (JOES-PC, 3440x1440 Dell U3425WE, Windows 11,
i7-6700K). v1 (C# on .NET 10) is the current implementation, on branch `main`; the PowerShell proof
of concept it replaces still runs the desktop today (see "POC (retired soon)" below) and is
deleted only once every item in the phase 6 parity gate is checked off.

## Non-negotiables from the owner

These predate the rewrite and still hold, unchanged, for whatever is on screen:

- **Nothing resident but the one process that has to be.** No tray-app-plus-widget-engine model
  (no Rainmeter, no Wallpaper Engine): one process (`deskwall.exe`) sleeps between ticks with no
  polling, wakes on a timer or a system event, and goes back to sleep. Measure any change to tick
  cost (`deskwall tick --measure` prints wall/CPU per stage) -- do not assert it.
- **Measured, not eyeballed.** Icon placement, padding, legibility: verify with a pixel diff of
  the live desktop against the composed image (`deskwall verify`, see "Verifying a v1 change"; the
  POC's `verify.ps1` did the same job). The owner rejected his own hand-placed icons in favour of
  a derived constant.
- **Design doctrine gates apply** (`design-doctrine` skill), to every rendered layout and to the
  designer UI. The job statement is: *answer "is a drive about to fill" and "what was I playing"
  in one glance, on a surface that spends most of its life behind windows.* Already deleted under
  Gate 3: section headers, game titles, playtime hours, last-played dates, proportional-length
  disk bars. The clock duplicates the taskbar's; the owner overruled that objection deliberately —
  leave it.
- Right-hand margin only. Windows sit centred on the ultrawide and leave ~440 px each side.

## Backlog lives in GitHub issues

`JoePittsy/DeskWall` issues are the backlog: bugs, deferred ideas, parity-gate items. The docs in
`docs/` record what is true (behaviour, formats, measurements, decisions); they are not a to-do
list and not a work log.

- **Before starting work**, `gh issue list` (and `gh issue view <n>` for the one you are on).
  Reference it in commits and PRs (`Fixes #n` closes it on merge to `main`).
- **File an issue the moment you find something you are not fixing now** -- a bug seen in
  passing, a concern in a lane report, an idea the owner mentions, a "deferred" decision.
  Context collapses; issues do not. One issue per thing, imperative title, body says what was
  observed (measured, with numbers) and where (`file:line`, doc, layout).
- **Labels:** one type (`bug`, `feature`, `chore`, `parity-gate`) plus one or more `area:*`
  (`core`, `daemon`, `designer`, `sources`, `layouts`, `poc`, `build`). Parity-gate items also
  carry the "Phase 6 parity gate" milestone.
- The repo is public: no machine secrets, API keys or personal paths beyond what the repo
  already shows.
- **Plans, briefs and lane reports are scratch, not repo content.** Skills (superpowers
  brainstorming/writing-plans/subagent-driven-development) default to writing them under
  `docs/superpowers/`; write them under the gitignored `.superpowers/` instead and never commit
  them. When the work lands, what lasts goes where it belongs: a measurement or verified behaviour
  into `docs/` (or a gotcha here), a design decision into `docs/design-spec.md` or the relevant
  reference doc, a follow-up into an issue, the rest into the PR description.

## Layout of the repo and runtime (v1)

| Where | What |
|---|---|
| `src/DeskWall.Core/` | Library, native-AOT-safe, no UI. Model (`Layout/`), bindings (`Bindings/`), resolution (`Resolve/`), rendering (`Render/`, Direct2D/DirectWrite/WIC via CsWin32), sources (`Sources/`), shortcuts (`Shortcuts/`), scheduling (`Scheduling/`), the tick itself (`Tick/TickRunner.cs`), widget templates and expander (`Widgets/`). |
| `src/DeskWall.Daemon/` | `deskwall.exe`. `Program.cs` (subcommands including `migrate`), `DaemonLoop.cs` (resident lifecycle, widget-folder watch), `Host/` (hidden window, tray, waitable timer, layout-file watcher). |
| `src/DeskWall.Designer/` | `DeskWall.Designer.exe`, WPF, JIT (not AOT). `Model/` (document, undo, live source panels, settings), `Views/` (canvas with three depths, Layers panel, Insert panel, properties panel, XAML). |
| `tests/DeskWall.Core.Tests/`, `tests/DeskWall.Designer.Tests/` | xUnit. Each run gets its own `DESKWALL_HOME` (`tests/Shared/TestRun.cs`) so nothing touches the real runtime dir or another run. |
| `layouts/` | Starter and example layout JSON files, plus `layouts/README.md`. `column-system.json` (weather, hardware dials, Tailscale) is what JOES-PC runs since 2026-09-21. |
| `assets/weather/` | 28 WMO-code weather icons (Meteocons, MIT) referenced by layouts as `runtime:assets/weather/{0}.png`; the designer ships and copies them into the runtime dir, `assets/weather/README.md` has the mapping. |
| `docs/layout-format.md`, `docs/sources.md`, `docs/architecture.md` | Reference docs generated by hand from the code; read them, do not restate them here. |
| `docs/design-spec.md` | The v1 design: purpose, constraints, budgets (spec 1.2) and the reasoning behind the architecture. |
| `docs/measurements.md` | Measured numbers: the CsWin32 call shapes verified in the phase 1 spike, tick timings, and the "Phase 6 budget results" the budget tests fill in. |
| `docs/hearth-feed.md` | The contract Hearth (the owner's Playnite add-on, separate repo) must write for the `recent-games` widget. |
| `.superpowers/` | Gitignored scratch for agent plans and briefs. Nothing in it is authoritative. |
| `%LOCALAPPDATA%\DeskWall\` | Runtime dir (`Paths.RuntimeDir`, override with `DESKWALL_HOME`). Full file-by-file description: `docs/architecture.md`. Gitignored. |
| `poc/` | The retired-soon PowerShell proof of concept. See its own section below. |

## v1 gotchas

- **Branches:** `main` is the integration branch; work branches are `lane/<name>` (or Kepler's
  `kepler/<name>`). Before resuming an area, read its open issues and the history
  (`git log -- <path>`); old plans and reports are in git history, not the tree.
- **CsWin32 (`allowMarshaling: false`):** COM interface methods return `void` and throw
  `COMException`; static entry points return `HRESULT` and take `.ThrowOnFailure()`.
  `CoInitializeEx` returns `RPC_E_CHANGED_MODE` on .NET's MTA main thread: treat as success
  (`Com.EnsureInitialized`). Callbacks need `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]`.
  Target `net10.0-windows10.0.19041.0` or CA1416 errors on Windows 8+ APIs. The full verified
  shape list is `docs/measurements.md`.
- **The installed build is self-contained JIT, not AOT.** `%LOCALAPPDATA%\Programs\DeskWall\`
  (the HKCU Run entry's `deskwall.exe run`) holds both executables in one folder; the designer is
  WPF and cannot be AOT, and a machine without the MSVC linker cannot publish an AOT daemon either
  (both JOES-PC and JOES-XPS-17 have it since 2026-09-28, so pass `-Aot` on both). Update it with `scripts/publish.ps1` (`-Aot` where the linker exists), never by
  hand: it carries the merge rule (designer's `WindowsBase.dll`, daemon's EventLog pair), stops the
  daemon with `deskwall stop`, and restarts it. Exercise it with a scratch `-InstallDir` and
  `-Home`, not the real install.
- **Native AOT publish needs the MSVC linker** (VS "Desktop development with C++"). Installed on
  JOES-PC on 2026-09-21 (VS Community 2026 18.9, MSVC 14.51, Windows SDK 10.0.26100), so budget
  numbers are now real: `docs/measurements.md` "Phase 6 budget results". `dotnet build` still
  runs the AOT analyzers (`IsAotCompatible`), so zero warnings there is meaningful -- but not
  proof: a reflection JSON fallback compiles and passes under JIT and fails only at runtime under
  AOT. Anything new the daemon deserialises needs a source-generated context and one real AOT
  publish that ticks it. Keep `StackTraceSupport=true` and `UseSystemResourceKeys=false` in the
  daemon csproj, or exception text in `deskwall.log` is useless under AOT.
- **Publishing from the Claude harness needs the VS Installer dir on PATH.** The harness sets
  `NoDefaultCurrentDirectoryInExePath=1`; VS 18's `VsDevCmd.bat` does `pushd` into the Installer
  dir and calls a bare `vswhere.exe`, which then fails on stderr, and ILCompiler's
  `findvcvarsall.bat` captures that stderr line into the linker path (`MSB3073 ... exited with
  code 123`). Fix, before `dotnet publish src/DeskWall.Daemon -c Release -r win-x64`:
  `$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"`.
  A normal terminal does not have the variable set and does not need this.
- **`deskwall.exe` is a WinExe.** PowerShell does not wait for it and does not see its console
  output unless you `Start-Process -Wait -NoNewWindow -RedirectStandardOutput` (what
  `tests/.../Budget/DaemonProcess.cs` does). Two one-shot ticks launched back to back without
  waiting run concurrently and race on the runtime dir (`restore.json` sharing violation).
- **Direct2D software rendering is WARP**: `d3d11.dll`/`D3D10Warp.dll` load, no vendor driver
  does. "GPU not used" means no hardware device. The D2D factory must be `MULTI_THREADED` or
  xUnit's parallel classes silently drop tests; the test assembly also disables parallelisation.
- **Tests run under their own `DESKWALL_HOME`** so they never write into the real
  `%LOCALAPPDATA%\DeskWall`, nor into another run's. `tests/Shared/TestRun.cs` (linked into both
  test projects, a module initializer) gives each test process `%TEMP%\deskwall-tests\<project>-<pid>-<id>\`
  with the home in `home\`, deletes it when the process exits, and on start sweeps run folders whose
  process is gone plus anything else in `deskwall-tests` untouched for a day. Every scratch file a
  test writes goes under `TestRun.Root`, never a fixed `%TEMP%` path: two runs on one machine (several
  worktrees at once is routine) used to share `deskwall-tests\home` and delete each other's
  `events.json` and base-cache raws. A test that writes the real runtime dir, or a fixed temp path, is
  a defect.
- **Core's timers come from an injected `TimeProvider`** (`EventBus`, `HardwareSource`,
  `NotificationSource`; `TimeProvider.System` by default). A test of a coalescing window, debounce
  or sampler passes a `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) and calls
  `Advance`, which runs due callbacks synchronously on the test's thread: never verify a window by
  sleeping through it. Only the timer moves to the provider; timestamps still come from `IClock`.
- **Display signature changes under RDP.** JOES-PC over Remote Desktop reports one primary
  monitor at 1920x1200, not 3440x1440; a layout authored for the ultrawide renders off-canvas
  until the layout store scales it (`LayoutScaler`, `docs/layout-format.md`). Screenshots of the
  console desktop are impossible from that session.
- **Content keys must quantise noisy values.** Disk free space wobbles below a pixel between
  reads; `ResolvedBar` keys the fraction at 0.1 percent or the skip path never fires.
- **Text paints outside its rect** (shadow ring, descenders, a trailing-aligned run wider than its
  box). Both the clip `Surface.DrawText` pushes and `ResolvedText.PaintBounds` are the *same call*
  into `Render/TextMeasure.cs` -- measured glyph ink inflated by `TextStyle.PaintMargin` (the effect
  ring only) -- so they cannot drift apart. Do not reintroduce a second derivation of either. The
  measurement is cached on (text, font, size, weight, align, box), deliberately not on colour.
- **`PaintBounds` is content-dependent and shrinks.** `"100%"` -> `"9%"` makes it smaller, so a
  changed component's dirty area is the union of its previous and current bounds --
  `FrameRenderer.RenderIncremental` adds `previousRects` for exactly this reason, and that line is
  load-bearing, not defensive. `TickRunner` also redraws a component whose measured bounds moved
  even when its content key did not, which is what catches a font being installed or updated.
- **`effectRadius` defaults to `"auto"`** = `max(1, round(size * 0.10))`. A fixed radius is wrong at
  both ends: 6 px is a drop shadow on a 64 px clock and a dark crust that closes the counters on a
  13 px label. An explicit number still wins, and `"auto"` must never be scaled by `LayoutScaler`
  or the factor lands twice -- once on the radius, once on the `size` it derives from.
- **Downscaled bitmaps need `HIGH_QUALITY_CUBIC`, not `CUBIC`.** Measured against a bicubic
  reference on a 96->56 icon, mean error per channel: bilinear 0.71, plain `CUBIC` **1.06** (its 4x4
  kernel rings at this ratio), `HIGH_QUALITY_CUBIC` 0.20. `BaseCache` is the deliberate exception
  and passes `Resample.Fast`: the base photo is only a ~1.12x downscale, and the good filter costs
  +163 ms of cold start (draw 236 -> 399 ms) against a 500 ms budget. **Large upscales are the
  other exception:** its cost scales with destination area (two gradient strips stretched over
  the canvas: 45-59 ms cubic, 3-5 ms linear), so `Surface.DrawSurface` draws a pure upscale of
  250,000 px or more linearly. Icon-sized upscales stay cubic; the `image-fits` and
  `repeater-auto` goldens fail if every upscale goes linear.
- **A signal is not a refresh.** `EventBus.Signal` (and `ISignalSource.Changed`) only wakes the
  daemon; the tick then refreshes what `Scheduler.IsDue` says is due. A signalling source must
  also report "due now" from `NextDue` while a change is pending, and clear it in `RefreshAsync`.
  Without the flag the first `audio` build repainted nothing for 8 volume changes; never cleared,
  it pins the daemon at `Scheduler.MinDelay`, four ticks a second.
- **The previous frame is never held in memory** by the daemon: it is 20 MB at 3440x1440 against
  a 10 MB budget. `frame.raw` is one read per tick.
- **POC and v1 both name desktop slots with non-breaking spaces.** They must not both own the
  same slot; v1's starter layouts claim slot 8 upward so they never collide with the POC's
  0..3. See the POC section below for the commands to disable/re-enable the POC task when a live
  check genuinely needs it isolated.
- **The single-instance lock is per runtime dir, not per session.** `Local\DeskWall.Daemon` for
  the default home (so the installed daemon and a bare `deskwall run` behave exactly as before)
  and `Local\DeskWall.Daemon.<hash>` for any `--home`. That is what lets a scratch daemon, and
  the budget tests, run beside the owner's live one -- before this they silently collapsed into
  "already running; asked it to refresh" and measured nothing.
- **The event pipe is per runtime dir too, because Windows would let two daemons share one.** A
  second process may create another instance of an existing named pipe when the ACL allows it,
  so with one shared name two daemons on two homes would both listen and a producer would land
  on whichever was next. `RuntimeInstance.EventPipeName` therefore gives it the lock's suffix:
  `DeskWall.Events` for the default home (every script and doc one-liner unchanged),
  `DeskWall.Events.<hash>` for any `--home`; `deskwall --home <dir> pipe` prints it. A test or a
  scratch daemon must never construct `EventPipeServer` with an explicit `DeskWall.Events` -- on
  JOES-PC that adds an instance to the live daemon's pipe and can steal the owner's events.
- **A named-pipe client can beat `ConnectNamedPipe` and its data is not lost, only unreadable.**
  An instance is connectable the moment `CreateNamedPipe` returns; a producer that connects,
  writes and disconnects before the server asks for a connection makes the connect fail with
  ERROR_NO_DATA ("the pipe is being closed"). Measured: one or two lines lost in every 20
  connect-write-disconnect sends. The bytes are still in the instance's buffer, and the way to
  reach them is to wrap the handle in a second `NamedPipeServerStream` with `isConnected: true`
  -- which is only legal on a **non-overlapped** handle, because an asynchronous one is already
  bound to the completion port and binding it twice throws. That is why `EventPipeServer` is
  synchronous with its own thread. Pre-arming more instances does not fix it; Windows will hand
  a client to a listening-but-not-yet-connected instance.
- **Providers cross into `SourceRegistry` on the tick thread only.** The registry is not
  synchronised; `EventBus` is. `DaemonLoop.SyncProviders` is the one crossing point, called just
  before the resolve. Never call `SetProvider` from the pipe thread or a bus callback.
- **`deskwall run` always paints the real wallpaper** -- there is no `--no-apply` for it, only for
  `tick`. A scratch `run` therefore takes the desktop over until the live daemon's next
  content change (the clock, so within a minute). Budget-style checks restore it explicitly.
- **Layout format is v2 (linked copies) on JOES-XPS-17 since 2026-09-29, still v1 on JOES-PC.**
  `deskwall migrate [--check] [<path>...]` converts v1 stamped instances to v2 linked copies. The
  backup is named `<file>.v1.json` (e.g. `column-system.v1.json`), never overwritten. `--check`
  prints the conversion without writing. See `docs/layout-format.md` "Copies".
- **Agents must never open the designer on the live runtime dir** (`%LOCALAPPDATA%\DeskWall`).
  Use a scratch `DESKWALL_HOME` environment variable. Designer screenshots via the in-process
  rendering harness (see `%TEMP%\dw-*/live.ps1`), because `PrintWindow` returns blank in agent
  sessions over RDP.

## Verifying a v1 change

```powershell
dotnet build                                      # AOT analyzer warnings are real even under JIT
dotnet test                                       # excludes Category=Budget and Category=Desktop
deskwall tick --layout layouts\clock-disks.json --force --measure --no-apply --no-shortcuts
deskwall verify                                   # per-slot arrow padding + clock crop; exit 0 OK, 4 mismatch, 1 cannot run
```

The default `dotnet test` leaves out two categories, via `tests/deskwall.runsettings` (both test
projects use it): `Budget` and `Desktop`. `Desktop` is every test that changes the live
interactive session or needs it unlocked -- writes `.lnk` files into the real desktop folder, moves
icons, switches the desktop folder flags, captures the screen, shows a window, or starts a resident
`deskwall run`.
Run them only on a desktop you may disturb, by name:
`dotnet test tests/DeskWall.Core.Tests --filter Category=Desktop` (and the same for
`tests/DeskWall.Designer.Tests`). Any command-line `--filter` replaces the runsettings filter
rather than adding to it, so a hand-written filter must exclude `Desktop` itself
(`--filter "FullyQualifiedName~Foo&Category!=Desktop"`). A new test that touches the live session
without the trait is a defect, like one that writes the real runtime dir.

`Session` tests only *read* the live session -- monitor enumeration, `IDesktopWallpaper::GetWallpaper`,
icon spacing and size, the position of a missing item -- so they are in the default run. Each
returns early when what it reads is absent (no monitors, no desktop folder view). A test that reads
the session but might also change it, even only on a regression, is `Desktop`. So is one that
depends on the session being unlocked: the screen-capture tests stay `Desktop`, because a capture of
a locked session is all black and `BitBlt` can fail on a disconnected RDP session.

`--no-apply --no-shortcuts` keeps a scratch tick from touching the live wallpaper or desktop
icons; drop them (and use a scratch `--home`) only when actually exercising the real thing.
`deskwall verify` prints `left pad N, bottom pad N` and the diff box size per shortcut slot (wants
5/5 and an arrow-sized box) and saves `verify-desktop.png`, `clock-now.png` and `verify-log.txt`
to the runtime dir, the same job the POC's `verify.ps1` did. It needs desktop icons visible and a
real shortcut on screen, so on a machine where the folder view is unavailable or icons are hidden
it exits 1 with that reason rather than reporting every slot missing.

## POC (retired soon)

Everything in this section describes `poc/`: PowerShell 5.1 + System.Drawing, hard-coded to one
3440x1440 display at 100% scaling. It still runs the desktop today via the `DeskWall Tick`
scheduled task and stays enabled until the phase 6 parity gate closes
(GitHub milestone "Phase 6 parity gate", #2-#16; each issue carries its runbook), at which point the task is
disabled and `poc/` is deleted in its own commit. Nothing here applies to `src/`.

Paths below are relative to `poc/`.

### Layout of `poc/`

| Where | What |
|---|---|
| `data.ps1` | Dot-sourced by everything. Exports `$Games`, `$Disks`, `$CoverRects`, `$Rects` (per-widget), geometry constants, `$DataDir`, `$DataSource`. |
| `compose.ps1` | The per-minute entry point. `-Apply -Force -Only a,b`. |
| `widgets\<name>.ps1` | Defines `Render-<name>($gr, $w, $h)`; draw at 0,0; return a content key string. |
| `shortcuts.ps1` | Creates/places the four cover shortcuts. Called by compose's games `After` hook. |
| `DeskIcons.cs` | COM interop: `[DeskIcons]::Position(paths, xs, ys)`, `::Get(path)`, `::Spacing()`. |
| `verify.ps1` | Screenshot of the right column + arrow-padding pixel diff + clock crop. Run after any layout change. |
| `tick.vbs`, `install-task.ps1` | Scheduled task plumbing. Task name `DeskWall Tick`. |

Runtime state (shared location, not `poc`-specific): `%LOCALAPPDATA%\DeskWall\` --
`state.json`, `base.png/.key`, `tiles\*.png/.key`, `deskwall.jpg`, `blank.ico`, `restore.txt`
(pre-DeskWall wallpaper), `playnite-config.backup.json`, verify screenshots. Gitignored. v1 uses
the same directory for its own, differently-named files (`docs/architecture.md`); the two do not
currently collide on any file name.

### Disabling the POC task for a live v1 check

Only when a check genuinely needs the POC not to be writing the wallpaper or the desktop at the
same moment (both can set the wallpaper; see README.md's "The proof of concept"). Always
re-enable it afterward, in a `finally` if scripted:

```powershell
Disable-ScheduledTask -TaskName "DeskWall Tick"
# ... the v1 check ...
Enable-ScheduledTask -TaskName "DeskWall Tick"
```

### Gotchas that cost time on 2026-09-20

**PowerShell 5.1**
- Variables are case-insensitive: `$h` silently overwrote `$H`. Use distinct names.
- Never name a parameter `$args`; it is an automatic variable and your value vanishes.
- `ConvertTo-Json` expands `[datetime]` into an object. Store dates as `.ToString('o')`.
- `ConvertFrom-Json` emits a JSON array as ONE pipeline object. Assign to a variable, then
  `foreach` over it. `@(... | ConvertFrom-Json)` does not unroll it.
- The Claude harness blocks `Remove-Item` with wildcard-looking paths in inline commands.
  Put such logic in a script file instead, or avoid it.
- Bash heredocs do not parse in the PowerShell tool. Use the Bash tool for git commits.
- **No non-ASCII in `.ps1` files.** PS 5.1 reads BOM-less UTF-8 as ANSI; an em dash becomes
  `a-c-` and a right-quote byte is accepted as a string terminator, which breaks parsing far from
  the actual line. `.ps1`/`.cs` are saved UTF-8 *with* BOM as a second line of defence.
- **`tick.vbs` must be plain ASCII with NO BOM.** VBScript fails at (1,1) "Invalid character"
  on a BOM, wscript shows a modal error dialog every minute, the task stays "running"
  (0x41301) and every following tick is skipped (`MultipleInstances IgnoreNew`). This
  happened on 2026-09-20 after a blanket re-encode; never bulk-re-encode `*.vbs`.

**Playnite**
- Locks `%APPDATA%\Playnite\library\games.db` exclusively while running. Close cleanly with
  `Playnite.DesktopApp.exe --shutdown` (takes ~1 s), read, relaunch. `data.ps1` falls back to
  `state.json` when locked, so normal ticks never need Playnite closed.
- Read with Playnite's own `%LOCALAPPDATA%\Playnite\LiteDB.dll` (v4.1.4). Collection is
  `Game` (not `games`). Fields: `_id` (Guid), `Name`, `LastActivity` (UTC), `IsInstalled`,
  `Hidden`, `CoverImage` (relative to `library\files`), `PluginId`, `GameId` (= Steam appid
  when PluginId is `CB91DFC9-B977-43BF-8E70-55F46E410FAB`). Absent keys are absent, not null:
  use `ContainsKey`.
- `LastActivity` only updates for sessions Playnite launched. Steam-launched sessions are
  merged from `C:\Program Files (x86)\Steam\userdata\<id>\config\localconfig.vdf`
  (`"<appid>" { ... "LastPlayed" "<unix>" }`), regex-parsed. Setting Playnite's
  `PlaytimeImportMode` to 2 (Always) and completing a library update did **not** fix it with
  Steam plugin 2.44 -- unresolved; the merge is the workaround. Config backup is in the
  runtime dir. (v1's `http`/Steam-API path sidesteps this entirely; see `docs/sources.md`.)
- Launch a game: `Playnite.DesktopApp.exe --start <_id guid>`.

**Desktop icons**
- Position via `IFolderView::SelectAndPositionItems` (see `DeskIcons.cs`). Requires
  snap-to-grid **off** (owner turned it off); otherwise positions snap to a 76x98 grid.
- Windows draws the shortcut-arrow overlay on a 48 px icon as a 13x13 square at
  `(item.x + 0, item.y + 40)`. Owner wants it exactly 5 px from each cover's bottom-left.
  (v1 measures this instead of assuming it: `deskwall calibrate`, `Calibration.Seed()` seeds the
  same value.)
- Shortcut names are 1..4 non-breaking spaces (U+00A0) so no label renders. Icon is a fully
  transparent 256 px PNG-in-ICO. Slot N is always cover N; only targets change.
- Icon positions are lost when the resolution changes (Apollo streaming does this). The
  games `After` hook re-places them whenever covers change; re-run `shortcuts.ps1` otherwise.
- Removing the arrow overlay globally needs HKLM `Shell Icons` value `29` + Explorer restart
  (admin). Not done; owner has not asked. (Not in v1 either; see spec section 7.)

**Wallpaper**
- `SystemParametersInfo(0x14, 0, path, 3)` with an **unchanged path** does reload the image. (v1
  uses `IDesktopWallpaper::SetWallpaper` instead, per-monitor; same "unchanged path still
  reloads" behaviour, measured again in the spike results.)
- Output is JPEG q92 (~2 MB) because it is written every minute; PNG was 15 MB.
- The base photo is the static Spotlight asset `image_3.jpg`; Spotlight itself is off. (Every v1
  starter layout points at the same asset.)
- Screenshots via `Graphics.CopyFromScreen` work from this session; `Shell.Application
  .MinimizeAll()` / `.UndoMinimizeALL()` around it exposes the desktop.

### Open threads (POC-specific)

- Playnite last-played not importing from Steam (see above). Check Add-ons > Steam for an
  authentication prompt. Moot for v1's Steam-`http`-source path; still relevant if/when a
  Playnite-backed `file` source recipe is built (see "Widget ideas" below). Tracked as #69.
- Text over the photo has only a 1 px shadow; legibility on bright areas is marginal. (Reassessed
  for v1 on 2026-09-21 and fixed there: the cause was a *fixed* 6 px blur applied at every font
  size, which on a 13 px label is a crust rather than a shadow. v1's radius is now proportional --
  see "v1 gotchas". The POC keeps its 1 px shadow.)
- Steam VDF is regex-parsed; a nested block before `LastPlayed` would break it.
- The 37 pre-existing desktop icons (game .url files, tool shortcuts) were catalogued but
  **not** deleted; owner was going to. Public-desktop ones need admin. Tracked as #70.
- Widget ideas the owner liked but deferred: days-since-last-crash (event log 41/1001) --
  **shipped in v1** as `system.daysSinceCrash` (`docs/sources.md`); pending-reboot flag (also
  shipped: `system.pendingReboot`) and Tailscale (shipped in `column-system.json`). Still
  deferred, each an issue: Apollo state #42, downloads in flight #43, repo status #44.
- Hearth (`~/source/repos/Hearth`, the owner's Playnite add-on) shares the Playnite domain; v1's
  `file` source is the intended seam for it to feed through without any Playnite- or
  LiteDB-specific code in DeskWall itself (spec 4.3, `docs/sources.md`). The feed plugin is #35.
