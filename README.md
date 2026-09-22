# DeskWall

Wallpaper-as-widgets for a Windows 11 desktop. Slow-changing state (a clock, disk headroom,
recently played games) is painted into a static image and set as the wallpaper once a minute, so
nothing stays resident between renders and no GPU surface is ever kept alive. Transparent desktop
shortcuts are placed over parts of the image to make them clickable.

**Status:** v1 (C# on .NET 10) is the current implementation, in `src/`. It has not yet reached
the parity gate against the PowerShell proof of concept it replaces (`poc/`, still running the
desktop today) -- see "The proof of concept" below.

Full design: `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md`. This file is the
human-facing overview; `CLAUDE.md` is the agent hand-off notes (gotchas, conventions).

## What it renders

The owner's own layout (`layouts/steam-recent.json` and `layouts/clock-disks.json`), right-hand
margin only so it stays visible beside a centred window and disappears under a maximised one:

- **Clock** -- `HH:mm`, Segoe UI Light 64 pt.
- **Continue playing** -- covers of the four most recently played Steam games, each cover a
  click-to-launch desktop shortcut (`steam://rungameid/<appid>`).
- **Disk headroom** -- one row per fixed drive: letter, GB free, a bar that turns red past a
  configurable used-fraction threshold.

None of that is hard-coded: what to draw, where, and where its data comes from are declared in a
layout JSON file (`docs/layout-format.md`), not in code. The three "starter" layouts under
`layouts/` are the same column with pieces removed, offered by the designer on first run.

## Install and first run

There is no installer. From a build or a published `deskwall.exe`:

```powershell
deskwall install     # HKCU Run entry, records the current wallpaper for uninstall, starts the daemon
deskwall layouts set layouts\clock-disks.json   # register a layout for this display (no secrets needed)
```

`deskwall install` starts the daemon immediately, so you do not have to sign out to see it work.
With no layout registered for the current display, the daemon logs "no layout ... waiting" and
leaves the wallpaper alone (spec 3.2: no layout means no frame, never a blank canvas). Registering
`steam-recent.json` instead needs two secrets first -- see `layouts/README.md`.

No admin rights are needed anywhere in this flow. `deskwall` never creates a scheduled task; it is
a resident process started once at sign-in (`HKCU\...\Run`), which is the opposite of the proof of
concept's model -- see "The proof of concept" below for why that changed.

### The designer

`DeskWall.Designer.exe` edits layout files against a live preview rendered by the same code the
daemon uses, with source and property panels and a binding picker. It is a separate process that
runs only while its window is open and never talks to the daemon directly -- it edits the same
files (`layouts.json`, layout files, `secrets.json`, `settings.json`) that the daemon watches and
hot-reloads. Its widget editor builds a reusable widget out of parts and saves it into
`%LOCALAPPDATA%\DeskWall\widgets\`, where it joins the gallery beside the shipped ones
(`docs/layout-format.md` "Your own templates, and the widget editor").
**The designer's main window (`MainWindow.xaml`, Phase 5 Task 8) is still being
built** at the time of writing; the panels and canvas it will host already exist and have their
own model tests (`tests/DeskWall.Designer.Tests`), but there is no way yet to open the whole
shell as a user would. Full plan: `docs/superpowers/plans/2026-09-20-deskwall-v1-phase5-designer.md`.

## Layouts

A layout is one JSON file: a base image, the sources it needs (time, disks, a Steam API call, an
RSS feed, a local file, a shell command, whatever), and the components placed on the canvas in
physical pixels, each property either a literal or a binding into a source's published values.

- `docs/layout-format.md` -- every component type and property with its default, the binding
  grammar, repeater semantics (auto cell sizing, overflow, clamping), display-signature scaling.
- `docs/sources.md` -- every source type, its settings, what it publishes, how failure and
  staleness behave, the remote image cache, the secrets file.
- `layouts/README.md` -- the five files under `layouts/`, one sentence each, and how to supply
  the two Steam secrets `steam-recent.json` needs.

```powershell
deskwall tick --layout layouts\clock-disks.json --force --measure   # render once, print timings, do not need a registered store entry
deskwall layouts list                                                # what is registered, and what this display resolves to
deskwall shortcuts                                                   # read-only: planned vs actual desktop-icon positions
```

## Pushing a value in

Everything above is pulled on a schedule. Anything running as you can also push: write one JSON
line to `\\.\pipe\DeskWall.Events` and it lands in the value tree under a name nothing had to
declare.

```powershell
$p = New-Object IO.Pipes.NamedPipeClientStream '.', 'DeskWall.Events', 'Out'
$p.Connect(2000); $w = New-Object IO.StreamWriter $p; $w.AutoFlush = $true
$w.WriteLine('{"source":"build","data":{"status":"green"}}')
$p.Dispose()
```

Bind a component to `build.data.status` and the wallpaper repaints within about half a second.
The last record of every provider is remembered across a restart, the designer's providers panel
lists what each one publishes, and the pipe is restricted to your own account -- which also means
a layout that binds a `shortcut` target to a pushed value will launch whatever that value says.
The whole thing is `docs/sources.md`, "Pushed values: events".

## Architecture, in short

Three things: the resident daemon (`deskwall.exe`, hidden window, no polling, one waitable
timer), the designer (a separate process, files only, runs while open), and `DeskWall.Core` (the
shared library everything else is built from, native-AOT-safe). The full tick pipeline --
resolve, skip-if-unchanged, draw, encode, apply, reconcile shortcuts -- and where every runtime
file lives is `docs/architecture.md`.

## Budgets, and what is actually measured today

Spec 1.2 sets a cost table (idle working set under 10 MB, a clock-only tick under 60 ms wall / 40
ms CPU, cold start under 500 ms, under 100 handles, under 5 threads at idle) that budget tests are
assert against a **native AOT** `deskwall.exe`. The first AOT run on the reference machine
(JOES-PC, 2026-09-21) met two rows and missed three; a memory fix wave the same day (no managed
staging copies of the frame, a compacting collection after every tick) brought idle commit from
48 MB to 8.2 MB and the clock-only tick from 146 to 92 ms. Cold start 99 ms, zero idle CPU and
8.2 MB idle commit are in budget; 279 handles / 9 threads and a clock-only tick of 92 ms wall /
62 ms CPU are not. The rows, the caveats and what each finding appears to be are in
`docs/architecture.md`'s budget section and
`docs/superpowers/plans/2026-09-20-phase1-spike-results.md` ("Phase 6 budget results"). Nobody
should read "meets budget" into the two open rows; they are findings, not a budget to raise.

For comparison, the proof of concept it replaces measured about 370 ms wall / 190 ms CPU per
tick and about 5 s cold start (spec 1.2, from the POC's own `compose.ps1` timing output).

## Verify

`deskwall verify` is the pixel-diff half of "measured, not eyeballed": minimise windows,
screenshot the primary monitor, diff it against the composed frame, report per-shortcut arrow
padding against the wanted 5 px and a clock crop, exit non-zero on any mismatch. **This command
is being added in another lane** (phase 6 plan, Task 1) and is not present in this worktree's
`Program.cs` at the time of writing; `deskwall shortcuts` (read-only slot/position reporting) and
`deskwall calibrate` (measures the shell's arrow-overlay offset once per icon size/scale and
writes `calibration.json`) already exist and cover part of the same ground.

## Uninstall

```powershell
deskwall uninstall
```

Stops the running daemon, removes the `HKCU\...\Run` entry, deletes the desktop shortcut slots
this build owns (recorded in `shortcuts-owned.json` -- see "The proof of concept" for why this is
scoped rather than a blanket sweep of the desktop), restores the folder flags (auto-arrange,
snap-to-grid) it may have turned off, and restores the wallpaper that was set before `deskwall
install` ever ran. No admin rights needed.

## The proof of concept

`poc/` is a PowerShell 5.1 + System.Drawing tool that did the same job first and **still runs the
desktop today**, via a scheduled task (`DeskWall Tick`) ticking once a minute. It is not legacy
code kept for reference; it is the thing currently painting JOES-PC's wallpaper, and it stays
enabled until every item in the phase 6 parity gate (`docs/superpowers/plans/2026-09-20-deskwall-v1-phase6-parity.md`)
is checked off, at which point the task is disabled and `poc/` is deleted in its own commit.

Until then, v1 and the POC coexist deliberately on the same desktop:

- Both name desktop-icon slot files with non-breaking spaces. The POC owns slots 0-3; v1's
  starter layouts claim slot 8 upward specifically so the two never fight over the same `.lnk`
  (`layouts/README.md`, "Shortcut slots"). `deskwall uninstall` only ever deletes slots recorded
  in `shortcuts-owned.json` -- a blanket sweep of every non-breaking-space icon on the desktop
  would take the POC's still-working icons with it.
- Both can set the wallpaper. Running both in earnest at once would fight over
  `SystemParametersInfo`/`IDesktopWallpaper`; verifying v1 against a live desktop means disabling
  the POC task for the duration and re-enabling it afterward (see `CLAUDE.md`'s POC section for
  the exact commands), never deleting it.

See `poc/` and the "POC (retired soon)" section of `CLAUDE.md` for what it does and the gotchas
specific to it (Playnite's LiteDB access, the Steam VDF merge, PowerShell 5.1 encoding traps).
Nothing in that section applies to `src/`.
