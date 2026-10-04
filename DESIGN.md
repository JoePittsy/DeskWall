# DeskWall design system

Derived from the shipped `layouts/alpine-rice.json` (30 September 2026). The desktop is an
Experience surface with one Operate strip: the photograph leads, the data recedes into the right
margin, and colour carries state, never decoration.

## World

The alpine ridge photograph, graded by phase of day (`assets/alpine/ridge-<phase>.jpg`, made by
`scripts/phase-photos.ps1` from the one dusk frame). Night and dusk dominate; day is neutral. There
are no full-canvas washes: mood comes from the photo, not from tinted overlays.

## Palette (Nord-derived, written `#AARRGGBB`)

| Role | Value | Where |
|---|---|---|
| Ink | `#E6ECEFF4` | values, titles, the clock |
| Dim | `#99D8DEE9` | labels, artists, secondary lines |
| Accent (frost) | `#FF88C0D0` | the ridge, dials, drive fills, progress |
| Secondary | `#FF5E81AC` | the CPU foothills line and its 10 % area |
| Warn | `#FFEBCB8B` | reboot pending, days since crash, 85-95 % dials, valley lights (at 70 %) |
| Danger | `#FFBF616A` | muted ridge, 95 %+ dials, hot snowfields, the drive-full wash (30 %) |
| Track | `#4DD8DEE9` | dial and bar tracks; the unlit ridge is `#59D8DEE9` |

Weather layers (rain, snow, fog, storm sky, thunder) use Ink or `#2E3440` at 25-45 % alpha. Stars are
Ink at 70 % / 45 %, night only.

## Type

One family, Bahnschrift. Light 160 for the clock (centred over the main peak, x≈1763); Regular 32/22
for now-playing title/artist; 22/12 for dial value/label; 20 for drive rows; 18 for the state lines.
Weight 400 everywhere except nothing. No text effects: legibility comes from the dark ground.

## Lines and glow

Ridge and foothills 3 px, glow 12 / 8 px at strength 0.3 / 0.25, round caps. Dials 4 px. The
progress bar 4 px with no track.

## Layout

Right column x 3000-3420: weather, four 96 px dials at y 176, drives at 330, battery/reboot/crash
lines at 524-616, all right-aligned. Bottom-left: 160 px art at (40, 1200), title and artist beside
it. Sun 140 px and moon 120 px on their arcs, no halos.

## Provenance

`scripts/rice-layout.ps1` derives this layout from `layouts/alpine-vision-photos.json`; every
value above lives there. Judge changes with `scripts/gallery.ps1 -Layout layouts/alpine-rice.json`
(`docs/superpowers/plans/gallery-rice/all.png` is the shipped montage).

## Second world: vapor (`layouts/vapor.json`)

Same structure, different world. The wallpaper is synthesised by `scripts/vapor-wallpaper.ps1`
from the ridge trace: a three-stop gradient sky per phase, a 330 px striped sun setting behind the
main peak at (2560, 640), the mountain as a flat silhouette, a cyan perspective grid on the valley
floor from y 1000. Palette: ink `#F2F6E9FF`, dim `#B3B9A6D6`, accent hot pink `#FFFF71CE` (ridge,
dials, drives), secondary cyan `#FF01CDFE` (foothills), warn peach `#FFFFB86C`, danger `#FFFF3860`,
tracks `#40F6E9FF`. The layout's own sun, moon and heat snowfields are dropped: the wallpaper's sun
is the hero and a flat silhouette shows every speckle. Derive with
`scripts/rice-layout.ps1 -Theme vapor -Out layouts/vapor.json`; judge with
`scripts/gallery.ps1 -Layout layouts/vapor.json` (`docs/superpowers/plans/gallery-vapor/all.png`).

Switching themes is two copies (a `deskwall theme` subcommand is #31): back up `%LOCALAPPDATA%\DeskWall\column-system.json`, then copy `layouts/<theme>.json` over it.
