# Layouts

A layout is one JSON file: a base image, the sources it needs, and the components placed on the
canvas in physical pixels. These are authored for 3440x1440 at 100 percent; the daemon scales a
layout proportionally when the display signature has no layout of its own.

| File | What it shows |
|---|---|
| `clock-disks.json` | Clock top right, one row per fixed drive bottom right. No network, no secrets. |
| `steam-recent.json` | The same column with the four most recently played Steam games between them, covers from Steam's CDN, each cover a click-to-launch shortcut. Needs two secrets. |
| `starter-column.json` | The Designer's first-run "column" card. Identical to `steam-recent.json`; needs the same two secrets before the covers appear. |
| `starter-clock.json` | The Designer's first-run "clock" card. Clock only, top right; no sources beyond time, no secrets. |
| `starter-blank.json` | The Designer's first-run "blank" card. Base image only, nothing drawn on top. |

The three `starter-*.json` files are what `FirstRun` offers when the layout store has no entry for
the current display: it copies the chosen one into `runtime/layouts/<signature>.json`, scaled to
the actual signature by `LayoutScaler`, and opens it in the designer. They are otherwise ordinary
layout files - open one directly with `deskwall tick --layout` like any other.

## Steam secrets

`steam-recent.json` calls Steam's public `GetRecentlyPlayedGames` endpoint. It needs a Web API
key and your 64-bit SteamID, neither of which belongs in a layout file. Put them in
`%LOCALAPPDATA%\DeskWall\secrets.json`:

```json
{ "steamKey": "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", "steamId": "76561198000000000" }
```

- Key: https://steamcommunity.com/dev/apikey (any domain name; it is not checked).
- SteamID64: your profile URL if it is numeric, or https://steamid.io with your profile link.

The layout references them as `{secret:steamKey}` and `{secret:steamId}`; the daemon substitutes
them at request time and never writes them to a log. A wrong key shows up in the log as `403 from
https://api.steampowered.com/...{secret:steamKey}...` with the placeholder, not the key.

## Using a layout

```powershell
deskwall layouts set layouts\steam-recent.json    # register for the current display (Phase 2 command)
deskwall tick --force --measure                   # render once
deskwall shortcuts                                # verify icon placement (Phase 3 command)
```

Until the daemon (`deskwall run`) exists, `deskwall tick --layout layouts\clock-disks.json --force`
renders a file directly.

## Base image

All starters point at the Windows Spotlight asset the POC used. Set `baseImage` to any JPEG or PNG
you like; `baseFit` is `cover` (crop to fill), `contain` (letterbox) or `stretch`.
