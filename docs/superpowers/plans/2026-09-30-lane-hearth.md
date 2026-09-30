# Lane: Hearth feeds DeskWall (recently played, no Steam key)

Worktree `D:\Source\Personal\DeskWall-hearth`, branch `spike/hearth-feed`. Read `CLAUDE.md`,
`docs/sources.md` (the `file` source and its rss/json parsing), `layouts/steam-recent.json`,
`scripts/playnite-recent.ps1` (from the spike) and `poc/data.ps1` (the LiteDB read). Hearth is the
owner's Playnite add-on at `C:\Users\JosephPitts\source\repos\Hearth` (if absent, check
`D:\Source\Personal\Hearth`; if neither exists, do part 2 only and say so).

Another agent is rewriting layouts, renderer and designer on `main`: **stay inside the Hearth
repo, `docs/sources.md`, `scripts/`, `layouts/widgets/` (new widget files only) and
`layouts/README.md`.** No publish, no touching `%LOCALAPPDATA%\DeskWall`.

Full permissions, never stop to ask; plain-English commits in each repo; both builds clean.

## 1. Hearth writes a feed
Add to Hearth: on any game start/stop or library change, write
`%LOCALAPPDATA%\DeskWall\feeds\hearth-recent.json` atomically (temp + rename): the 8 most recent
installed, non-hidden games with `id`, `name`, `lastPlayed` (ISO 8601), `cover` (absolute path),
`installed`, `playtimeMinutes`, `launch` (`Playnite.DesktopApp.exe --start <id>`), plus
`writtenAt`. Steam-launched sessions must merge Steam's `localconfig.vdf` LastPlayed exactly as
`poc/data.ps1` does, because Playnite does not record them (see the POC gotchas in `CLAUDE.md`).
Merge from Playnite's own ID for the Steam appid mapping. Guard every write with try/catch; an
add-on must never crash Playnite.

## 2. DeskWall consumes it
A `file` source recipe in `docs/sources.md` (`runtime:feeds/hearth-recent.json`, json, watch the
file so it is push not poll if the `file` source supports a watcher; if it does not, add one,
that is the one Core touch allowed, in `Sources/FileSource.cs` only). A widget
`layouts/widgets/recent-games.json`: a repeater of `image` cover + `shortcut` over it, 4 wide,
120 px covers, 8 px radius, one knob `count` (default 4), shortcuts claiming slots 8..11 (never
0..3, the POC owns those). Document it in `layouts/README.md`.

## 3. Prove it
Build Hearth; run its unit tests if any. Generate a fake feed with 4 entries and real cover PNGs
in a scratch `DESKWALL_HOME`, `deskwall tick --layout <a scratch layout using the widget> --force
--no-apply --no-shortcuts --measure`, and look at the rendered `deskwall.jpg`. Lane report at
`docs/superpowers/plans/2026-09-30-lane-hearth-report.md`: what to install on JOES-PC (Hearth
build, where the extension goes), and the exact `deskwall verify` run to confirm slot padding.
Leave both branches unmerged.
