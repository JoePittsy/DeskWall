# Lane: multi-monitor and the lock screen

Status: not merged -- `spike/multi-monitor` stopped at 8 commits plus WIP, no report; tracked in
#30. Auto-switching the layout on a display change builds on it: #74.

Worktree `D:\Source\Personal\DeskWall-monitors`, branch `spike/multi-monitor`. Read `CLAUDE.md`
(all of it), `docs/architecture.md`, `src/DeskWall.Core/Tick/TickRunner.cs`,
`src/DeskWall.Daemon/DaemonLoop.cs` and `Host/`, `Layout/LayoutStore.cs`, `DisplaySignature`,
`LayoutScaler`, and `docs/superpowers/plans/2026-09-30-spike-loud-report.md` (the desktop as it
stands). Other lanes are running in sibling worktrees; you own Tick, Host, LayoutStore, Shortcuts
and the designer's *display* handling. No publish, do not touch `%LOCALAPPDATA%\DeskWall`; use a
scratch `DESKWALL_HOME`. Full permissions, never stop to ask; plain-English commits; build clean.

Today: one layout per display *signature* (the whole set of monitors as one key), one frame.
Goal: **one layout per monitor, all painted every tick.** JOES-XPS-17 docked to the Dell U3425WE
has two: the 3440x1440 Dell and the laptop's own 1920x1200 panel. The landscape belongs on the
Dell; the laptop panel becomes a second surface (dials, now-playing, calendar, notifications).

## 1. Model
- `DisplaySignature` stays the key for the *set*; add a per-monitor identity (the
  `IDesktopWallpaper` monitor id, plus w/h/scale) and a `MonitorSet` snapshot.
- `layouts.json` maps `<set signature>` -> `{ "<monitor id or index>": "<layout path>", ... }`
  (keep the old single-string form loading as "primary only", migrate on save).
- A layout file may declare `"monitor": { "match": "primary|secondary|<id>|<WxH>" }` so a starter
  layout can say which screen it wants.

## 2. Tick
`TickRunner` composes **N frames** (one `frame.raw`/`frame-state.json`/`deskwall-<n>.jpg` per
monitor; keep today's names for monitor 0 so nothing else breaks), resolves each layout against
the *same* source tree once (sources are shared; do not refresh twice), and applies each with
`IDesktopWallpaper::SetWallpaper(monitorId, path)`. Shortcuts: desktop icons live on one virtual
desktop spanning monitors; positions are absolute, so a shortcut on the second monitor is offset
by that monitor's origin from `GetMonitorRECT`. Content keys and the dirty-rect skip path stay
per frame. `--measure` prints per-monitor rows.

## 3. Display changes
Dock/undock changes the set: the existing display-change handling re-resolves the layout set and
scales; a monitor that disappears gets its frame dropped and its wallpaper left alone; one that
appears with no layout gets the starter `clock-disks.json` scaled to it. Log clearly.

## 4. Designer
The canvas shows every monitor of the current set at its real offset (grey frames, labelled),
each with its own layout document; the layout switcher lists monitors; drag a part between
monitors moves it to the other document. Keep it simple: two documents open, one canvas.

## 5. Lock screen
`Windows.System.UserProfile.LockScreen.SetImageFileAsync` (WinRT, user-level, no admin) with the
primary frame after each *content* change, throttled to at most once per 10 minutes and skipped
when the session is locked (`WTSRegisterSessionNotification` / `WM_WTSSESSION_CHANGE` on the
hidden window). Setting `lockScreen: true` in `settings.json`. Verify by reading the lock screen
image path back (`LockScreen.OriginalImageFile`).

## 6. Prove and report
Scratch home, a two-layout set, `tick --measure` showing two rows; a screenshot of each frame
(half size) in `docs/superpowers/plans/gallery/monitors-*.png`; the designer canvas with two
monitors via the in-process harness (never the live home). Lane report at
`docs/superpowers/plans/2026-09-30-lane-monitors-report.md` including exactly what to run to go
live (the owner will merge and publish). Update `docs/architecture.md`.
