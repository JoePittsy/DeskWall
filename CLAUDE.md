# DeskWall — agent notes

Read this before touching anything. README.md is the human-facing overview; this file is the
stuff you would otherwise rediscover the hard way.

## What this is, in one sentence

A PowerShell 5.1 tool that paints slow-changing state (clock, recently played games, disk
headroom) into a static wallpaper image every minute, with transparent desktop shortcuts
placed over the game covers so they launch on click. Owner: Joe (JOES-PC, 3440x1440 Dell
U3425WE, Windows 11, i7-6700K). Branch `poc`; no `main` yet.

## Non-negotiables from the owner

- **Nothing resident.** No tray app, no Rainmeter/Wallpaper Engine. A scheduled task that
  runs for ~400 ms and exits is the accepted cost model. Measure any change to tick cost
  (compose.ps1 prints wall/cpu ms) — do not assert it.
- **Measured, not eyeballed.** Icon placement, padding, legibility: verify with a pixel diff
  of the live desktop against the composed JPEG (`verify.ps1`). The owner rejected his own
  hand-placed icons in favour of a derived constant.
- **Design doctrine gates apply** (`design-doctrine` skill). The job statement is: *answer
  "is a drive about to fill" and "what was I playing" in one glance, on a surface that
  spends most of its life behind windows.* Already deleted under Gate 3: section headers,
  game titles, playtime hours, last-played dates, proportional-length disk bars. The clock
  duplicates the taskbar's; the owner overruled that objection deliberately — leave it.
- Right-hand margin only. Windows sit centred on the ultrawide and leave ~440 px each side.

## Layout of the repo and runtime

| Where | What |
|---|---|
| `data.ps1` | Dot-sourced by everything. Exports `$Games`, `$Disks`, `$CoverRects`, `$Rects` (per-widget), geometry constants, `$DataDir`, `$DataSource`. |
| `compose.ps1` | The per-minute entry point. `-Apply -Force -Only a,b`. |
| `widgets\<name>.ps1` | Defines `Render-<name>($gr, $w, $h)`; draw at 0,0; return a content key string. |
| `shortcuts.ps1` | Creates/places the four cover shortcuts. Called by compose's games `After` hook. |
| `DeskIcons.cs` | COM interop: `[DeskIcons]::Position(paths, xs, ys)`, `::Get(path)`, `::Spacing()`. |
| `verify.ps1` | Screenshot of the right column + arrow-padding pixel diff + clock crop. Run after any layout change. |
| `tick.vbs`, `install-task.ps1` | Scheduled task plumbing. Task name `DeskWall Tick`. |
| `%LOCALAPPDATA%\DeskWall\` | Runtime: `state.json`, `base.png/.key`, `tiles\*.png/.key`, `deskwall.jpg`, `blank.ico`, `restore.txt` (pre-DeskWall wallpaper), `playnite-config.backup.json`, verify screenshots. Gitignored. |

## Gotchas that cost time on 2026-09-20

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
  `â€”` and the `”` byte is accepted as a string terminator, which breaks parsing far from
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
  Steam plugin 2.44 — unresolved; the merge is the workaround. Config backup is in the
  runtime dir.
- Launch a game: `Playnite.DesktopApp.exe --start <_id guid>`.

**Desktop icons**
- Position via `IFolderView::SelectAndPositionItems` (see `DeskIcons.cs`). Requires
  snap-to-grid **off** (owner turned it off); otherwise positions snap to a 76x98 grid.
- Windows draws the shortcut-arrow overlay on a 48 px icon as a 13x13 square at
  `(item.x + 0, item.y + 40)`. Owner wants it exactly 5 px from each cover's bottom-left.
- Shortcut names are 1..4 non-breaking spaces (U+00A0) so no label renders. Icon is a fully
  transparent 256 px PNG-in-ICO. Slot N is always cover N; only targets change.
- Icon positions are lost when the resolution changes (Apollo streaming does this). The
  games `After` hook re-places them whenever covers change; re-run `shortcuts.ps1` otherwise.
- Removing the arrow overlay globally needs HKLM `Shell Icons` value `29` + Explorer restart
  (admin). Not done; owner has not asked.

**Wallpaper**
- `SystemParametersInfo(0x14, 0, path, 3)` with an **unchanged path** does reload the image.
- Output is JPEG q92 (~2 MB) because it is written every minute; PNG was 15 MB.
- The base photo is the static Spotlight asset `image_3.jpg`; Spotlight itself is off.
- Screenshots via `Graphics.CopyFromScreen` work from this session; `Shell.Application
  .MinimizeAll()` / `.UndoMinimizeALL()` around it exposes the desktop.

## Verifying a change

```powershell
.\compose.ps1 -Apply -Force     # everything fresh
.\verify.ps1                    # screenshot + arrow padding per cover + clock crop
```
`verify.ps1` prints `left pad N, bottom pad N` per cover (want 5/5) and saves
`verify-desktop.png` and `clock-now.png` in the runtime dir. A stray JPEG-noise pixel can
inflate the diff bounding box; the arrow itself is always 13x13.

## Open threads

- Playnite last-played not importing from Steam (see above). Check Add-ons > Steam for an
  authentication prompt.
- Text over the photo has only a 1 px shadow; legibility on bright areas is marginal.
- Steam VDF is regex-parsed; a nested block before `LastPlayed` would break it.
- The 37 pre-existing desktop icons (game .url files, tool shortcuts) were catalogued but
  **not** deleted; owner was going to. Public-desktop ones need admin.
- Widget ideas the owner liked but deferred: days-since-last-crash (event log 41/1001),
  Tailscale/Apollo state, pending-reboot flag, downloads in flight, repo status.
- Hearth (`~/source/repos/Hearth`, the owner's Playnite add-on) shares the Playnite domain;
  the games widget could eventually reuse its data layer.
