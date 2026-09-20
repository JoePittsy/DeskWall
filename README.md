# DeskWall

Wallpaper-as-widgets for a Windows 11 desktop. Slow-changing state is painted into a static
image and set as the wallpaper, so there is nothing resident and nothing burning cycles
between renders. Transparent desktop shortcuts are placed over parts of the image to make
them clickable.

**Status: proof of concept.** PowerShell 5.1 + System.Drawing, hard-coded to one
3440x1440 display at 100% scaling.

## What it renders

Right-hand margin only, so it stays visible beside a centred window and disappears under a
maximised one. Top to bottom:

- **Clock** — `HH:mm`, Segoe UI Light 64px. Refreshed every minute by a scheduled task.
- **Continue playing** — covers of the four most recently played *installed* games from
  Playnite's library. Each cover has an invisible shortcut over it that launches the game
  through Playnite (`Playnite.DesktopApp.exe --start <id>`).
- **Disk headroom** — one row per drive: letter, GB free, and a bar of used space that turns
  red under 15% free.

Recency is the later of Playnite's own `LastActivity` and Steam's `LastPlayed` from
`userdata\<id>\config\localconfig.vdf`, because Playnite only records sessions it launched.

## Widgets and the compositor

Every widget is a function in `widgets\<name>.ps1` that draws into a transparent tile of its
own size at 0,0 and returns a content key. `compose.ps1` runs every minute and:

1. re-renders each tile whose age exceeds its schedule (`clock` 60 s, `disks` 300 s,
   `games` 600 s) or that is missing; if a widget's key changed it may run an `After` hook —
   `games` re-places the desktop shortcuts so icons never drift from the covers;
2. blits the cached full-size photo plus every tile into one ~2 MB JPEG and applies it.

Measured on an i7-6700K, 2026-09-20: composite-only tick ~370 ms wall / ~190 ms CPU
(dominated by the JPEG encode); clock tile adds nothing measurable; cold start with all
tiles, photo cache and shortcut placement ~5 s. Nothing stays resident between ticks.

Adding a widget = one file with `Render-<name>($gr, $w, $h)`, a rect in `data.ps1`, and a
line in `$Widgets`.

## Files

| File | Role |
|---|---|
| `data.ps1` | Shared data + geometry: reads Playnite's LiteDB (read-only, via Playnite's own `LiteDB.dll`), merges Steam recency, computes column layout. Caches a snapshot to the runtime dir and falls back to it when Playnite has the DB locked. |
| `compose.ps1` | The compositor described above. `-Apply` sets the wallpaper, `-Force` re-renders all tiles, `-Only clock,disks` limits which. Prints timing. |
| `widgets\clock.ps1`, `widgets\disks.ps1`, `widgets\games.ps1` | One render function each. |
| `shortcuts.ps1` | Creates one transparent-icon shortcut per cover (named with N non-breaking spaces so no label shows) and positions it via the shell's `IFolderView::SelectAndPositionItems`. |
| `DeskIcons.cs` | COM interop for the desktop view: get/set icon positions. |
| `tick.vbs` | Runs `compose.ps1 -Apply` with no console window. |
| `install-task.ps1` | Registers the `DeskWall Tick` scheduled task: every minute while logged on, and on logon. `-Uninstall` removes it. No admin needed. |

Runtime state lives in `%LOCALAPPDATA%\DeskWall`, not in the repo: `state.json` (library
cache), `base.png` + `.key` (scaled photo), `tiles\*.png` + `.key`, `deskwall.jpg` (the
wallpaper), `blank.ico`, `restore.txt` (the pre-DeskWall wallpaper path).

## Running

```powershell
.\compose.ps1 -Apply      # render due tiles, composite, set wallpaper
.\compose.ps1 -Force      # re-render everything (no apply)
.\shortcuts.ps1           # (re)create and place the four shortcuts by hand
.\install-task.ps1        # start the per-minute tick
.\install-task.ps1 -Uninstall
```

Requires: Playnite installed (for `LiteDB.dll` and the library), Steam, desktop icon
snap-to-grid **off**, 100% display scaling.

## Measured constants

- Display 3440x1440, taskbar 48px. Column 172px wide, 48px from the right edge; clock at
  y=48 (70px), covers from y=142, disk block bottom-anchored 40px above the taskbar.
- Windows paints the shortcut-arrow overlay on a 48px icon as a 13x13 square at
  `(item.x + 0, item.y + 40)`. `shortcuts.ps1` uses this to put the arrow exactly 5px from
  each cover's bottom-left corner.
- Setting the wallpaper to an unchanged path via `SystemParametersInfo` does reload it.

## Known gaps (POC)

- Playnite locks `games.db` exclusively; the cache covers it but a first run needs Playnite closed.
- Steam's `localconfig.vdf` is parsed with a regex, not a real VDF parser.
- Icon positions are lost on resolution change (e.g. Apollo streaming); slots are stable so
  re-running `shortcuts.ps1` restores them.
- Text over the photo has only a 1px shadow; legibility on bright backgrounds is marginal.
- The tick fires a second or two after the minute boundary, so the clock lags by that much.
- Playnite's own last-played stays stale for Steam-launched sessions even with play time
  import set to Always (Steam plugin 2.44); the Steam merge is the workaround.
