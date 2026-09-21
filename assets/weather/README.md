# Weather icons

96x96 PNGs, white on transparent, one per Open-Meteo WMO weather code, named `<code>.png`. Bound
from a layout as `weather.json.current.weather_code | "runtime:assets/weather/{0}.png"`
(`docs/layout-format.md` binding example 12); the `runtime:` prefix resolves against the runtime
directory (`Paths.InRuntime`), not the repo, so this folder must be copied to
`%LOCALAPPDATA%\DeskWall\assets\weather` before a layout that uses it renders correctly
(`layouts/README.md`).

## Source

Basmilius "Meteocons", https://github.com/basmilius/weather-icons, MIT licence (`LICENSE` in
this folder, copied verbatim from the upstream repo). Icons are `production/line/svg/*.svg`
fetched from the `dev` branch (`main` on that repo has no `production/` tree; the published set
lives on `dev`), the line (single-colour, monochrome) set as required by the design spec so the
icon reads on a photo. Fetched and rasterised 2026-09-21.

## Mapping (WMO code -> Meteocons file)

| WMO code(s) | Meteocons file | Note |
|---|---|---|
| 0 | `clear-day` | |
| 1 | `partly-cloudy-day` | fallback: Meteocons has no `mostly-clear-day` in this set |
| 2 | `partly-cloudy-day` | |
| 3 | `overcast` | |
| 45, 48 | `fog` | |
| 51, 53, 55 | `drizzle` | |
| 56, 57 | `sleet` | |
| 61, 63, 65 | `rain` | |
| 66, 67 | `sleet` | |
| 71, 73, 75, 77 | `snow` | |
| 80, 81 | `rain` | |
| 82 | `extreme-rain` | present upstream, used as specified |
| 85, 86 | `snow` | |
| 95 | `thunderstorms` | |
| 96, 99 | `thunderstorms-rain` | |

28 files total; codes that share an icon are duplicate copies of the same PNG, not symlinks (the
designer/daemon only ever open one file per code, so a duplicate costs disk space, not behaviour).
Day/night variants are out of scope (spec section 5): every code above maps to the day icon
regardless of `current.is_day`.

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

1. Fetch the SVGs listed in the mapping table from
   `https://raw.githubusercontent.com/basmilius/weather-icons/dev/production/line/svg/<file>.svg`.
2. Build the small `filter: brightness(0) invert(1)` HTML harness described above for each unique
   icon file (one HTML page per icon, not per code) and render it at 96x96 with a headless
   Chromium build (`chrome.exe --headless=new --disable-gpu --window-size=96,96
   --default-background-color=00000000 --screenshot=<out>.png <page>.html`), or, if the Playwright
   or chrome-devtools MCP browser tools launch cleanly in your session, `browser_navigate` +
   `browser_evaluate` drawing to a canvas and reading back a data URL is the originally intended
   path and produces the same pixels.
3. Copy the rendered PNG to every WMO code in the mapping table that shares it.
4. Re-run the pixel check (96x96, RGBA, single RGB colour `(255,255,255)` wherever alpha > 0)
   before committing.
