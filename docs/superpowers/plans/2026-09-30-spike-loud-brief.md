# DeskWall spike, round 2: make it judgeable, then make it LOUD

Repo `D:\Source\Personal\DeskWall`, branch `main`. Read `CLAUDE.md` for the gotchas (CsWin32,
AOT publish PATH fix, WinExe, `DESKWALL_HOME`, never open the designer on the live runtime dir).
Then read `docs/superpowers/plans/2026-09-30-spike-vision-brief.md` and
`docs/superpowers/plans/2026-09-30-spike-report.md` for what round 1 built, and look at
`docs/superpowers/plans/2026-09-30-spike-result.png`.

The owner's verdict on round 1: **"you understand my disappointment."** The screenshot is his old
desktop with a faint line on the ridge. Round 1 followed a brief that said "faint", "subtle", "low
alpha" fifteen times, and every bold layer it built is invisible tonight by construction (rain with
no rain, hot snow on a cool GPU, stars before dark, cairns without Playnite).

**The design doctrine is switched off for this round.** Do not write "subtle", "faint" or "low
alpha" anywhere. Build loud first; the owner will dial it back himself with the gallery from Part A.
Full permissions, nobody watching, never stop to ask. Commit each part in plain English. Keep
`dotnet build` clean; tests may be updated or deleted when a deliberate change breaks them.

## Part A: eyes. `--preview` and a scenario gallery

1. `deskwall tick --preview key=value,...` pins values in the source tree *after* the sources
   refresh and before the resolve (add a `PreviewOverrides` step in `TickRunner`; dotted paths,
   e.g. `time.dayFraction=0.98,time.now=23:30,weather.json.current.weather_code=63,
   audio.volume=0.8,audio.muted=false,hardware.gpuTempFraction=0.92,hardware.cpu=0.9,
   disks.worstUsedFraction=0.93,battery.fraction=0.15,battery.charging=false`). Numbers become
   `NumberValue`, `true`/`false` `BoolValue`, else `TextValue`. Time-derived fields
   (`time.now`, `time.date`, `dayFraction`) are pinned together when `time.at=HH:mm` is given.
   Also pin the hardware history lists to a plausible curve when `hardware.cpu` is pinned (so
   the foothills line has a shape).
2. `scripts/gallery.ps1`: copies the live layout to a scratch home and renders **eight** scenes
   to `docs/superpowers/plans/gallery/<scene>.png` (half-size, 1720x720), each a
   `tick --preview` with `--no-apply --no-shortcuts`: `midnight-clear`, `dawn`, `noon`, `dusk`,
   `storm` (weather 65, dayFraction 0.6), `snow` (75), `machine-on-fire` (cpu 0.95, gpuTemp 0.95,
   ram 0.9), `drive-full-and-muted` (worst 0.95, muted). Plus `now-playing` with a media title,
   artist and a generated placeholder art PNG. Run it after every change in Part B and **look at
   the PNGs** (Read them). A montage `gallery/all.png` (2 columns x 5 rows) at the end.

## Part B: LOUD. Rewrite `layouts/alpine-vision.json` composition

Targets are visual and measured against the gallery, not against taste:

- **Ridge (volume)**: stroke 7, glow 24, glow alpha per stroke high enough that the halo is
  obvious in a half-size PNG. Lit colour Blends on `time.dayFraction`: white noon, amber dusk,
  electric ice-blue night. Muted: the whole ridge and its glow go red, not just the line.
- **CPU foothills**: a real second skyline, stroke 5, glow 20, in a colour that is *not* the
  ridge's (violet/magenta at night, teal by day), with the filled area under it at ~35% alpha.
  Bind history so the shape has drama; with `--preview hardware.cpu=0.9` it should peak.
- **Sky grade**: strong. Midnight is a deep indigo wash you can *see* over the photo (alpha
  55-65%), dawn a rose band, dusk amber to plum, noon clear. It must change the mood of the photo,
  not tint it.
- **Stars**: 400, three sizes (round-cap stubs of 1.5, 2.5, 4 px), visible from dusk (alpha
  climbing from dayFraction 0.72), full white at night, a few with a Blend to warm colour. Only
  above the ridge trace.
- **Sun**: 260 px soft disc with a second, larger glow disc behind it (2 images), rising bottom-left
  at 0.25, apex at 0.5, setting bottom-right at 0.75, colour white -> amber -> red near the
  horizon. **Moon**: 200 px, bright, with a soft halo, follows the inverse arc. Both should be the
  second thing you see after the clock.
- **Weather**: rain 600 diagonal strokes, alpha 55% in a storm; snow 400 dots; fog a proper
  white/grey wash at 45%, thunder (95-99) adds a violet flash tint to the sky grade. Visible in
  the `storm` and `snow` scenes without squinting.
- **Machine heat**: both snowfields go from white through orange to red with alpha up to 85% at
  the top of the ramp; at `machine-on-fire` the peaks should look molten. Add a **heat shimmer**
  above the main peak: a shape bar of 40 wavy `C` curves, alpha bound to gpuTemp.
- **Drive full**: the deep-red sky wash at 60% alpha from 0.85, and the drive bars themselves go
  red with a glow. In `drive-full-and-muted` the desktop must look *wrong* at a glance.
- **Now playing**: title 40 px, artist 24 px, art 200 px with an 8 px radius, bottom-left above
  the utility glyphs, with a progress bar under it if the media session exposes position
  (`GlobalSystemMediaTransportControlsSessionTimelineProperties`; poll-free: update on the
  `TimelinePropertiesChanged` event only).
- **Right margin**: use it. A vertical stack, top to bottom: weather (bigger icon, 48 px temp),
  the four dials at 96 px with value text inside, drive bars with GB text, valley-peer count as
  text, battery text when on battery. Clock stays as is.
- **Utility glyphs**: "reboot pending" in 22 px amber with a glow, not 12 px grey.
- Everything that Blends on `dayFraction` should get its stops from **one** place: add
  `time.phase` (a text: `night|dawn|day|dusk`) to `TimeSource` and prefer Step rules on it
  where a colour just needs four states, so the palette is editable in one Blend per layer.

## Part C: proof and ship

1. `deskwall tick --repeat 10 --measure` on the final layout in a scratch home. Warm draw under
   400 ms; if a layer costs more than 60 ms on its own, find out why (geometry with thousands of
   figures should be one `PathGeometry`, built once and cached on the `ResolvedBar` content key).
   Report the per-layer numbers.
2. Publish (`scripts/publish.ps1 -Aot`, VS Installer on PATH, designer not running), apply the
   layout live (keep the `before-*` backups), confirm `deskwall.log` shows a clean
   `LayoutChanged` tick.
3. Run the gallery one last time against the *installed* build; write
   `docs/superpowers/plans/2026-09-30-spike-loud-report.md` with the montage embedded, per-scene
   notes, tick cost, and what you would dial back first and why. Commit.
