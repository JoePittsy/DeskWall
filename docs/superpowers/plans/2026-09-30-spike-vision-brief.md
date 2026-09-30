# DeskWall spike: "the full vision" — brief for an autonomous agent

Repo: `D:\Source\Personal\DeskWall` (branch `main`, clean at `6dc36bc`). Read `CLAUDE.md` first: it
carries the gotchas (CsWin32 shapes, AOT publish PATH fix, `deskwall.exe` is a WinExe, tests run
under `DESKWALL_HOME`, never open the designer on the live runtime dir). Then `docs/layout-format.md`,
`docs/sources.md`, `docs/architecture.md`.

The owner (Joe) is away and has said, verbatim: this is a spike, go wild, skip writing tests, he
wants to come back to "the most sickass desktop ever". So: **build fast, keep the build green
(`dotnet build` zero errors; existing tests may be updated or deleted if a deliberate change breaks
them), commit each feature, publish at the end so the desktop shows it.**

## What is on screen today

Machine: JOES-XPS-17 docked to a Dell U3425WE, 3440x1440 @ 100%. Live runtime dir
`%LOCALAPPDATA%\DeskWall`; live layout `column-system.json` (format v2). Backups already taken:
`column-system.before-spike.json` and `column-system.before-ridge.json` — **do not overwrite them**.

The layout: base photo `C:\Users\JosephPitts\Downloads\5168918.jpg` (3440x1440, alpine ridge at
dusk, sky above y≈350, dark foreground hills below y≈800), a huge Algerian clock top-centre, three
small hardware dials above it, weather + Tailscale top-right, C:/D: drive bars bottom-right, and a
new **ridge volume bar**: a `bar` component `id: ridge` with `shape` = 227-point SVG path traced
along the skyline, `thickness: 5`, `glow: 8`, `fraction` bound to `audio.volume`, rect
`[-11,346,3461,485]`. The daemon watches the layout file and repaints within a second of a save, so
editing the JSON is the way to change the desktop. `deskwall.log` in the runtime dir shows each tick.

Sources in the layout: `time`, `audio`. Add others as needed (`hardware`, `system`, `disks` are
zero-config; see `docs/sources.md`). No `secrets.json` exists, so nothing needing an API key (Steam).

Features already available to compose with (all shipped today, see `docs/layout-format.md`):
- Bindings with **Step rules** (`?<0.3=a,<0.7=b,*=c`), **Blend rules** (`~0=12,1=48`, numbers or
  ARGB colours interpolate) and maps (`?true=x,false=y`). Any property on any part is bindable,
  including numbers (size, weight, opacity, thickness, sweep).
- `bar` with `shape` (SVG path M/L/H/V/C/Z), `thickness` (0 = filled), `glow`.

## Deliverables, in priority order

Commit after each. Measure tick cost with
`deskwall tick --layout <live layout> --force --measure --no-apply --no-shortcuts --home <scratch>`
(copy the layout into a scratch home first). Draw stage should stay well under 500 ms warm.

### 1. Zero-code composition (layout JSON only) — do these first, they are cheap and visible
- **Sky follows the day**: a full-screen `bar` (rect `[0,0,3440,1440]`, `fraction: "1"`, z below
  everything, above the base) whose `fill` and `track` are bound to `time.dayFraction` with a Blend:
  deep navy with ~35% alpha at 0 and 1 (midnight), fully transparent by ~0.3 and until ~0.7, warm
  amber ~20% alpha around 0.78 (dusk), back to navy by 0.85. Result: the photo cools at night.
- **Stars at night**: one `bar`, `fraction: "1"`, `thickness: "2"`, `shape` = ~150 `M x,y h0.01`
  micro-segments at random positions in the sky region (above the ridge trace — the trace's
  points are in the ridge component's `shape`; keep stars above them), `fill` bound to
  `time.dayFraction` with a Blend that is white ~70% alpha at night and alpha 0 by day. Round caps
  make the stubs render as dots.
- **Mute is visible**: ridge `fill` bound `audio.muted | "?true=#FFD13438,false=#E6FFFFFF"`.
- **Utility glyphs** (add `system` source): a small text bottom-left that is empty unless it
  matters — `system.pendingReboot | "?true=reboot pending"` and `system.daysSinceCrash` as
  "N days since crash" only when < 7 (Step rule to empty string otherwise).

### 2. `battery` source (new, small)
`GetSystemPowerStatus` via CsWin32 (`NativeMethods.txt` in DeskWall.Core). Publish `percent`
(0..100), `fraction` (0..1), `charging` (bool), `onBattery` (bool), `minutesLeft` (or -1).
Periodic, every 60 s. Register in `SourceFactory`, add to `SourceForms.BuiltIn` and
`Insert.DefaultSources` in the designer, add labels in `ValueCatalog.Labels`, document in
`docs/sources.md`. On screen: a fourth small dial next to the hardware dials, fill Step rule
red below 20%, amber below 50%; opacity Blend or map so it fades when `charging` and full.

### 3. `line` component (sparkline) + hardware history — the big one
- `HardwareSource`: publish `cpuHistory`, `ramHistory` (and `gpuHistory` when a GPU reader exists)
  as `ListValue` of records `{ "v": <0..1> }`, oldest first, from the existing `RollingWindow`
  (add an ordered snapshot method; ring of `window/sample` slots, default 30).
- New `LineDef : ComponentDef` (JSON type `"line"`): `values` (binding to a list), `field` (default
  `"v"`), `stroke` colour, `thickness` (default 2), `glow` (default 0), `min`/`max` (defaults 0/1),
  `baseline` (bool: also fill under the line at low alpha). Resolve to a `PathData` (oldest at
  left, y inverted) and draw with the existing `Surface.DrawPath` — no new rendering code.
  Content key = the joined rounded values.
- Register everywhere a `ComponentDef` type is enumerated: `ComponentDef` `[JsonDerivedType]`,
  `ComponentProperties`, `LayoutResolver`, `FrameRenderer`, designer `PropertySchema.Editors`,
  `PropertyRows`, `PartKind`/Insert panel if cheap (otherwise editing-only is fine).
- On screen: **CPU history drawn as a second ridge across the dark foreground hills**, full width,
  y band ≈ 820..1000, cool blue stroke with a glow, faint fill under it. It should read as a
  mountain silhouette echoing the real one.

### 4. `media` source — now playing (time-box: 60 minutes, drop it if AOT fights back)
WinRT `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` (TFM already
`net10.0-windows10.0.19041.0`, projections come with it). Event-driven — implement `ISignalSource`
like `AudioSource` so it pushes, never polls. Publish `title`, `artist`, `album`, `playing`
(bool), `app`, and `art` (thumbnail saved as PNG into the runtime dir, published as an
`ImageValue` path; rewrite only when the bytes change). Must still `dotnet publish -c Release
-r win-x64` with AOT (`scripts/publish.ps1 -Aot`); if CsWinRT AOT breaks, spend at most 20
minutes on it, then remove the source and note it in the report. On screen: title/artist in small
text just under the ridge line's left end, art 96 px beside it, all empty when nothing plays.

### 5. Designer polish (if time remains)
- Rules rows: colour swatch button per row that opens the existing `ColorPicker`; live preview of
  the rule result while editing (apply on change, coalesced, not only on Done).
- `line` in the Insert panel's Parts row.

## Ground rules that still hold in psycho mode
- One resident process, no polling for anything that can be pushed; measure tick cost, do not
  assert it.
- Right-hand margin is where windows never sit (~440 px each side); the centre is usually covered.
- The design doctrine still gates *legibility*: faint by default, nothing shouting; the base photo
  is the hero. Decoration is allowed this once, but keep it subtle (stars/sky at low alpha).
- Never open `DeskWall.Designer.exe` against the live runtime dir. Use a scratch `DESKWALL_HOME`.
- UK English in anything written.

## Finish
1. `dotnet build` clean. Update `docs/layout-format.md` and `docs/sources.md` for anything added.
2. Commit each feature separately on `main` with a plain-English message.
3. Publish: `$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH";
   .\scripts\publish.ps1 -Aot` (check `DeskWall.Designer.exe` is not running first).
4. Edit the live `column-system.json` to add the new components (the daemon repaints on save).
   Save a half-size PNG of the resulting `%LOCALAPPDATA%\DeskWall\deskwall.jpg` to
   `docs\superpowers\plans\2026-09-30-spike-result.png` and look at it.
5. Write `docs\superpowers\plans\2026-09-30-spike-report.md`: what shipped, what was dropped and
   why, tick cost before/after (`--measure` numbers), and how to revert
   (`copy column-system.before-spike.json column-system.json`).

---

# Part 2 — the actual vision. Do Part 1 first, then keep going until this is real.

The photo is not a background. It is the instrument. Every number DeskWall knows should be
readable *from the landscape itself*, and the landscape should live: it has a time of day, weather,
heat, and the owner's recent life on it. Build in this order.

### 6. Bindable geometry — things that move
`rect` is a fixed int today. Make `x`, `y`, `w`, `h` individually bindable (`PropertyValue`
alongside the literal `Rect`; resolver reads them through `PropertyReader.Number` with the literal
as fallback; `LayoutScaler`, the designer's geometry rows and the content key all follow). With
Blend rules this gives motion for free:
- **A sun that crosses the sky**: image (a soft radial disc PNG you generate, ~160 px) whose `x`
  is `time.dayFraction | "~0.25=-200,0.75=3440"` and `y` a Blend that arcs (low at 0.25, high at
  0.5, low at 0.75), alpha 0 at night. At night the same slot is a **moon** (second image, alpha
  inverted). Colour of the sun disc Blends white → amber toward 0.75.
- **The ridge glow follows the light**: glow colour amber at dusk, ice-blue at night, white by day.

### 7. Weather on the mountain (the layout already fetches Open-Meteo)
Read the weather widget's http recipe; it has `weather_code`, `is_day`, temperature. Add:
- **Fog**: a full-screen bar, white, alpha bound to `weather_code` by a Step rule (codes 45/48 →
  ~25%, else 0).
- **Rain / snow**: two shape bars of ~300 tiny diagonal stubs (`M x,y l4,12`) / round dots,
  fill alpha via Step rules on `weather_code` (rain 51–67 and 80–82; snow 71–77 and 85–86).
  Faint. The photo should look *wet*, not like a screensaver.
- **Snow caps that heat up**: trace the bright snowfield region of the main peak (same
  luminance-walk technique as the ridge, but thresholding *bright* inside the rock band) to a
  closed filled path; fill it red with alpha Blend on `hardware.gpuTempFraction` (0 at 0.5, ~45%
  at 0.9). The peaks glow when the GPU cooks. Same for CPU on the second peak.

### 8. The job statement, finally on screen — "is a drive about to fill" and "what was I playing"
- **The sky answers the disk question**: bind the sky-grade bar's `track` colour through a
  *second* layer: a full-screen bar whose fill alpha is a Step rule on the *worst* drive's
  `usedFraction` (add `disks.worstUsedFraction` to `DisksSource`): alpha 0 below 0.85, a deep red
  wash at 0.85+, pulsing is not possible and not wanted. When a drive is about to fill, the whole
  desktop is wrong-coloured. That is the one-glance answer the owner asked for in the spec.
- **Recently played, as clickable covers on the mountain**: no Steam key exists, so read
  **Playnite** directly: a `command` source running a PowerShell script (ship it in
  `scripts/playnite-recent.ps1`) that loads `%LOCALAPPDATA%\Playnite\LiteDB.dll` and reads
  `%APPDATA%\Playnite\library\games.db` collection `Game` (fields `_id`, `Name`, `LastActivity`,
  `CoverImage`, `IsInstalled`, `Hidden`; the POC in `poc/data.ps1` did exactly this — copy its
  LiteDB code; note the file is locked while Playnite runs, so copy it to temp first and read the
  copy). Emit JSON: the four most recent installed, non-hidden games with absolute cover paths and
  `Playnite.DesktopApp.exe --start <id>` targets. Compose a `repeater` of `image` + `shortcut`
  parts (see `layouts/steam-recent.json` for the shape) sitting in the right-hand margin low on
  the foreground hills, like cairns. Covers 120 px wide, 8 px radius, the ridge line passing
  behind them. Run `deskwall verify` afterwards for the shortcut padding (wants 5/5).

### 9. The designer becomes a drawing tool
- **Pen tool**: in the canvas, a mode where clicks add points over the base photo and Enter drops
  a shaped `bar` with that path (rect = bounds, path relative to it). Double-click closes the
  figure (filled). This is how the owner traces the *next* ridge without a script.
- **Trace tool**: "Trace skyline here" — the luminance-walk from the ridge trace, on a band the
  owner drags out on the canvas, produces the path. Ship the trace algorithm in Core
  (`Render/SkylineTrace.cs`) so both this and a `deskwall trace` subcommand use it.
- Rules editor: colour swatches per row, live preview (from Part 1 §5) — now required, not
  optional, because everything above is rules.

### 10. Ambient details, if there is still fuel
- **Contrail**: the plane in the photo at (~940,340 full-res) gets a 2 px line that grows with
  `disks.drives[C].usedFraction`… no. Leave the plane alone. Doctrine.
- **Tailscale peers as lights in the valley**: the `Tailscale Running` text becomes N dots on the
  dark valley floor, one per online peer (`tailscale status --json` via command source), each a
  round-cap stub. Subtle, warm white.
- **Uptime as the ski run**: the bright ski piste (≈(600,470)→(630,570) half-res) traced as a
  filled path whose alpha Blends on `system.uptime` (fresh boot bright, week-old faint).
- **Minute hand**: the second ridge (CPU) is fine; do *not* animate the clock.

### Report
Same finish as Part 1, plus a before/after pair of half-size screenshots and the `--measure`
table for the final layout. If draw exceeds ~400 ms warm, say which layer costs what (comment
layers out one at a time) and leave the expensive one disabled with a note rather than shipping
a slow tick. The owner would rather have eight subtle layers that cost 150 ms than one that
costs a second.
