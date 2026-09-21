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
| `column-system.json` | Clock, Leeds weather, Tailscale state, four hardware dials (CPU/GPU/RAM load, GPU temperature) and the drives row, all in the right-hand column. No Steam covers. See "column-system.json requirements" below. |

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

## column-system.json requirements

`column-system.json` is the widgets recipe from `docs/superpowers/specs/2026-09-21-widgets-hardware-weather-design.md`:
a `hardware` source (native CPU/RAM/GPU sampler), a Leeds `http` weather source, and a `command`
source that shells out to Tailscale. Each has its own requirement:

- **Weather.** The `http` source calls Open-Meteo with hard-coded coordinates for Leeds
  (`latitude=53.8008&longitude=-1.5491` in the source's `url`). To point it at a different town,
  edit those two numbers in the layout file - Open-Meteo needs no key and the request is unauthenticated.
  The weather icon (`sky` component) binds `weather.json.current.weather_code |
  "runtime:assets/weather/{0}.png"`; the `runtime:` prefix resolves against the runtime directory,
  not the repo, so **`assets/weather` must be copied to `%LOCALAPPDATA%\DeskWall\assets\weather`**
  before this layout renders icons (the designer does this automatically whenever it copies a
  starter; a manual `deskwall tick --layout layouts\column-system.json` needs the folder copied
  by hand first, or the icon area draws the missing-image fallback plate).
- **Tailscale.** The `vpn` line needs Tailscale installed at its default path,
  `C:\Program Files\Tailscale\tailscale.exe`; edit `sources[].settings.command` if yours is
  elsewhere. Without it the `command` source's own failure means the line falls back to its
  default (spec 3.2: a missing binding is never an exception).
- **GPU dials.** The `gpuDial`/`gpuPct` and `tempDial`/`tempC` components bind `hardware.gpu` and
  `hardware.gpuTempFraction`/`hardware.gpuTempC`, which the `hardware` source only publishes when
  it finds an NVIDIA GPU (NVML). On a machine with no GPU, or a non-NVIDIA one, those two dials and
  their text simply draw at their bound properties' defaults - no error, no NVML on the box needed
  to try the rest of the layout.
- **Hardware dials need the daemon running for a minute.** A one-shot `deskwall tick` always shows
  the four dials empty, because the `hardware` source's 10-second sampler has not taken a reading
  yet on that process's first (and only) tick; the resident daemon fills them once its own sampler
  has been running for a bit.

## Shortcut slots

A `shortcut` component names a `slot`: the desktop icon it owns. One `.lnk` per slot lives on the
desktop, named with non-breaking spaces so no label draws, and DeskWall only ever deletes the slots
recorded in `shortcuts-owned.json`. Inside a repeater the declared slot is a *base*: the child of
item `n` gets `slot + n`, so the four-cover column shipped here claims 8..11.

Slots 0..3 are deliberately left alone. The v0 PowerShell proof of concept still writes exactly
those four file names every minute, and two tools rewriting the same `.lnk` makes Explorer
re-enumerate the desktop, which is how icon positions get lost. Pick a base of 8 or above until the
POC is retired, and give two shortcut components in one layout non-overlapping ranges - a duplicate
slot is a layout error.

## Command source stderr

A `command` source publishes its `stderr` verbatim. A CLI that fails and echoes its own argument
list back - the usual shape of a usage error - therefore publishes the *substituted* value of any
`{secret:...}` in `args` into a value a text component can draw on the wallpaper. Nothing else in
DeskWall does that: logs and exceptions carry the template. If a command takes a secret, do not bind
a component to its `stderr`.

## Using a layout

```powershell
deskwall layouts set layouts\steam-recent.json    # register for the current display
deskwall tick --force --measure                   # render once, using whatever is registered
deskwall shortcuts                                # read-only: planned vs actual desktop-icon positions
```

`deskwall tick --layout layouts\clock-disks.json --force` renders a file directly, bypassing the
layout store entirely -- useful for trying a file out before registering it, or in a script.
Add `--no-apply` to render without touching the wallpaper, and `--no-shortcuts` to also leave the
desktop icons alone (both are safe against a scratch `--home`; without them a tick is a real tick).

## Base image

All starters point at the Windows Spotlight asset the POC used. Set `baseImage` to any JPEG or PNG
you like; `baseFit` is `cover` (crop to fill), `contain` (letterbox) or `stretch`.
