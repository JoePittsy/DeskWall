# DeskWall

Wallpaper-as-widgets for a Windows 11 desktop. A scheduled render paints slow-changing
state into a static image and sets it as the wallpaper, so there is nothing resident and
nothing burning cycles. Transparent desktop shortcuts are placed over parts of the image
to make them clickable.

**Status: proof of concept.** Everything is PowerShell 5.1 + System.Drawing, hard-coded
to one 3440x1440 display, and driven by hand.

## What it renders

Right-hand margin only, so it stays visible beside a centred window and disappears under
a maximised one.

- **Continue playing** — covers of the four most recently played *installed* games from
  Playnite's library. Each cover has an invisible shortcut over it that launches the game
  through Playnite (`Playnite.DesktopApp.exe --start <id>`).
- **Disk headroom** — one row per drive: letter, GB free, and a bar of used space that turns
  red under 15% free.

Recency is the later of Playnite's own `LastActivity` and Steam's `LastPlayed` from
`userdata\<id>\config\localconfig.vdf`, because Playnite only records sessions it launched.

## Files

| File | Role |
|---|---|
| `data.ps1` | Shared data: reads Playnite's LiteDB (read-only, via Playnite's own `LiteDB.dll`), merges Steam recency, computes cover geometry. Caches a snapshot to `%LOCALAPPDATA%\DeskWall\state.json` and falls back to it when Playnite has the DB locked. |
| `render.ps1` | Draws the wallpaper over the current Spotlight photo and saves `deskwall-real.png`. `-Apply` sets it as the wallpaper. |
| `shortcuts.ps1` | Creates one transparent-icon shortcut per cover (named with N non-breaking spaces so no label shows) and positions it via the shell's `IFolderView::SelectAndPositionItems`. |
| `DeskIcons.cs` | COM interop for the desktop view: get/set icon positions. |

Runtime state (cache, rendered PNGs, blank icon, restore note) lives in
`%LOCALAPPDATA%\DeskWall`, not in the repo.

## Running

```powershell
.\render.ps1 -Apply    # re-render and set wallpaper
.\shortcuts.ps1        # (re)create and place the four shortcuts
```

Requires: Playnite installed (for `LiteDB.dll` and the library), Steam, desktop icon
snap-to-grid **off**, 100% display scaling.

## Measured constants

- Display 3440x1440, taskbar 48px. Cover column 180px wide, 48px from the right edge.
- Windows paints the shortcut-arrow overlay on a 48px icon as a 13x13 square at
  `(item.x + 0, item.y + 40)`. `shortcuts.ps1` uses this to put the arrow exactly 5px
  from each cover's bottom-left corner.

## Known gaps (POC)

- Playnite locks `games.db` exclusively; the cache covers it but a first run needs Playnite closed.
- Steam's `localconfig.vdf` is parsed with a regex, not a real VDF parser.
- Icon positions are lost on resolution change (e.g. Apollo streaming); slots are stable so
  re-running `shortcuts.ps1` restores them.
- Text over the photo has only a 1px shadow; legibility on bright backgrounds is marginal.
- No scheduler yet; runs by hand.
