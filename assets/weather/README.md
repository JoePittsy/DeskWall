# Weather icons

96x96 PNGs, white on transparent, two per Open-Meteo WMO weather code: `<code>.png` for day and
`<code>-night.png` for night. Bound from a layout as
`weather.json.current.weather_code | "runtime:assets/weather/{0}.png"` (and `{0}-night.png`); the
`weather` widget draws both parts on one spot and shows one by an opacity Step on
`current.is_day` (`docs/layout-format.md` binding example 12); the `runtime:` prefix resolves against the runtime
directory (`Paths.InRuntime`), not the repo, so this folder must be copied to
`%LOCALAPPDATA%\DeskWall\assets\weather` before a layout that uses it renders correctly
(`layouts/README.md`).

## Source

Basmilius "Meteocons", https://github.com/basmilius/weather-icons, MIT licence (`LICENSE` in
this folder, copied verbatim from the upstream repo). Icons are `production/line/svg/*.svg`
fetched from the `dev` branch (`main` on that repo has no `production/` tree; the published set
lives on `dev`), the line (single-colour, monochrome) set as required by the design spec so the
icon reads on a photo. Day icons fetched and rasterised 2026-09-21; the two night icons
(`clear-night`, `partly-cloudy-night`) from the same tree, same licence, on 2026-10-04.

## Mapping (WMO code -> Meteocons file)

| WMO code(s) | Day (`<code>.png`) | Night (`<code>-night.png`) | Note |
|---|---|---|---|
| 0 | `clear-day` | `clear-night` | |
| 1 | `partly-cloudy-day` | `partly-cloudy-night` | fallback: Meteocons has no `mostly-clear-day` in this set |
| 2 | `partly-cloudy-day` | `partly-cloudy-night` | |
| 3 | `overcast` | `overcast` | |
| 45, 48 | `fog` | `fog` | |
| 51, 53, 55 | `drizzle` | `drizzle` | |
| 56, 57 | `sleet` | `sleet` | |
| 61, 63, 65 | `rain` | `rain` | |
| 66, 67 | `sleet` | `sleet` | |
| 71, 73, 75, 77 | `snow` | `snow` | |
| 80, 81 | `rain` | `rain` | |
| 82 | `extreme-rain` | `extreme-rain` | present upstream, used as specified |
| 85, 86 | `snow` | `snow` | |
| 95 | `thunderstorms` | `thunderstorms` | |
| 96, 99 | `thunderstorms-rain` | `thunderstorms-rain` | |

Only an icon that shows the sun has a night form here. The day icons for codes 3 and up are the
time-neutral Meteocons files (no sun in them), so their night file is a copy of the day file.
Meteocons does publish `overcast-night`, `fog-night`, `thunderstorms-night` and the like, but they
are night forms of the `-day` variants (`overcast-day`, `fog-day`, ...) that this mapping does not
use; switching to them would change the day icons too, so they are left out.

56 files total (28 codes x day and night); codes that share an icon are duplicate copies of the
same PNG, not symlinks (the designer/daemon only ever open one file per code, so a duplicate costs
disk space, not behaviour). Every code needs a `-night` file even where it equals the day one: the
night part draws `{0}-night.png` for whatever code is current, and a missing file draws the
fallback plate.

## Rendering notes

96x96, white (`#FFFFFF`) on a fully transparent background, verified per file: every pixel with
alpha > 0 is pure white, alpha itself carries the anti-aliased edge.

The repo has no SVG rasteriser and this session's Playwright/chrome-devtools MCP browser tools
both failed to launch (`Chromium distribution 'chrome' is not found` / `Could not find Google
Chrome executable`) -- neither tool's configured launch channel matches an installed browser on
this machine. The actual Chromium binary Playwright manages (downloaded under
`%LOCALAPPDATA%\ms-playwright\chromium-<rev>\chrome-win64\chrome.exe`) is present and works when
invoked directly, so icons were rasterised with that binary run headless from the command line
instead of through the MCP tool wrappers:

1. Each SVG is embedded (as-is, unmodified) as the `src` of an `<img>` in a tiny HTML page sized
   96x96 with `html, body { margin: 0; background: transparent; }`.
2. The `<img>` has `filter: brightness(0) invert(1)` -- `brightness(0)` turns every opaque pixel
   black while preserving alpha (including partial alpha on anti-aliased edges), `invert(1)` then
   turns black to white. This is the CSS equivalent of drawing the SVG to a canvas and compositing
   a white fill with `globalCompositeOperation = 'source-in'`; the two produce the same pixels.
3. `chrome.exe --headless=new --disable-gpu --window-size=96,96
   --default-background-color=00000000 --screenshot=<out>.png file:///<page>.html` renders the
   page and screenshots it. `--default-background-color=00000000` is required -- without it the
   screenshot composites onto opaque white and the transparency is lost.
4. Every output PNG was checked pixel-by-pixel (Pillow) before being copied into this folder:
   96x96, mode RGBA, and the only RGB value present anywhere alpha is nonzero is `(255, 255, 255)`.

## To regenerate

1. Fetch the SVGs listed in both columns of the mapping table from
   `https://raw.githubusercontent.com/basmilius/weather-icons/dev/production/line/svg/<file>.svg`.
2. Build the small `filter: brightness(0) invert(1)` HTML harness described above for each unique
   icon file (one HTML page per icon, not per code) and render it at 96x96 with a headless
   Chromium build (`chrome.exe --headless=new --disable-gpu --window-size=96,96
   --default-background-color=00000000 --screenshot=<out>.png <page>.html`), or, if the Playwright
   or chrome-devtools MCP browser tools launch cleanly in your session, `browser_navigate` +
   `browser_evaluate` drawing to a canvas and reading back a data URL is the originally intended
   path and produces the same pixels.
3. Copy the rendered PNG to every file name in the mapping table that uses it (`<code>.png` for
   the day column, `<code>-night.png` for the night column). The SVGs are animated (SMIL); the
   headless screenshot takes the first frame, so a re-render differs from the committed PNG by a
   few levels of alpha on moving edges (measured on `clear-day`: max 8/255).
4. Re-run the pixel check (96x96, RGBA, single RGB colour `(255,255,255)` wherever alpha > 0)
   before committing.
