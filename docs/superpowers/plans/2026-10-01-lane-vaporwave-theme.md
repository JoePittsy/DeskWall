# Lane: a second theme, vaporwave / pastel, new wallpaper and layout

Status: the theme is on `main` (`layouts/vapor.json`, `DESIGN.md` "Second world: vapor"); not
done: the `deskwall theme` subcommand (#31) and the lane report (#52).

Repo `D:\Source\Personal\DeskWall`, branch from `main` (after the merge-day brief's merges).
Read `CLAUDE.md`, `DESIGN.md` (the rice world: keep its *structure*, replace its *world*),
`scripts/rice-layout.ps1` (the transform pattern: derive, never hand-edit 40 components),
`scripts/phase-photos.ps1`, `scripts/gallery.ps1`, and the `deskwall trace` subcommand
(`Render/SkylineTrace.cs`). The owner asked for "pastel or vaporwave or something new: new
wallpaper and layout". This is a theme, so the result must be switchable: the layout store maps a
display to a layout file, so shipping `layouts/vapor.json` beside `alpine-rice.json` and swapping
`layouts.json` is the switch. Full permissions, never stop to ask; plain-English commits; build
clean; no publish; apply live only as the last step with a `column-system.before-vapor.json` backup.

## 1. The wallpaper
Source a wallpaper the owner can keep: a CC0/Unsplash-licence photograph or a generated image,
3440x1440 or larger, with a **clean horizon or skyline** the trace can follow (a city skyline at
dusk, a sea horizon with a low sun, a desert ridge). Vaporwave means: pink-to-violet sky gradient,
a large low sun, teal/cyan shadows, grain. Grade it with a new `scripts/phase-photos.ps1`
palette mode (`-Style vapor`) so night is deep violet, dawn peach, day pastel blue, dusk hot pink.
Store under `%DESKWALL_HOME%\assets\vapor\` with a `README.md` naming the source and licence.

## 2. The trace
`deskwall trace <photo> x y w h out.json` on the horizon band; the result becomes the volume line
(`bar` with `shape`, thickness 3, glow 16). Trace a second feature if the image offers one (a
skyline's rooftops, a pier) for the CPU history `line`.

## 3. The layout
Copy `scripts/rice-layout.ps1` to `scripts/vapor-layout.ps1` with the palette swapped:
ink `#F6E9FF`, dim `#B9A6D6`, accent hot pink `#FF71CE`, secondary cyan `#01CDFE`, warn peach
`#FFB86C`, danger `#FF3860`, tracks `#F6E9FF` at 25 %. Type: pick one geometric face with a
retro edge that is installed or can be shipped in the runtime dir; if nothing better exists,
`Bahnschrift Light` for the clock and note it as a fallback. Composition can move: the sun is the
hero here, so the clock may sit left and the column right, or the column may go bottom as a
strip. Stars become a sparse dot grid. Weather layers keep their alphas from rice, recoloured.

## 4. Judge and ship
`scripts/gallery.ps1 -Layout layouts/vapor.json -Out docs/superpowers/plans/gallery-vapor`; look
at every PNG; fix in one batch; look once more. `tick --repeat 10 --measure`. Add a
`deskwall theme <name>` subcommand that swaps the live layout to `layouts/<name>.json` with a
backup, so switching rice/vapor is one command. Report at
`docs/superpowers/plans/2026-10-01-lane-vapor-report.md` with the montage. Append the vapor
world to `DESIGN.md` as a second section rather than replacing the rice one.
