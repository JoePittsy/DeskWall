# DeskWall widgets: hardware dials, weather, Tailscale

**Date:** 2026-09-21. **Branch:** `v1`. **Owner:** Joe. **Status:** approved in conversation
("Number 1", then "just get it done"); the section-by-section approval rounds were waived.

Extends `2026-09-20-deskwall-v1-design.md`. Everything in that spec's non-negotiables (1.1) and
cost budget (1.2) still applies; this document adds one source type, one component type, two
layout recipes and one starter layout, and changes nothing else.

## 1. What the owner asked for, and what was decided

Asked: "add some more cool widgets, maybe an http weather source", then "average CPU / GPU / RAM
usage & temps on a miniature refresh cadence, average over the last 5 mins, radial dials", and
Tailscale / Apollo state.

Decided, in order, each by the owner:

| Question | Decision |
|---|---|
| Widgets | Weather, Tailscale state, hardware dials. (Days-since-crash and pending-reboot exist already and were not chosen.) |
| CPU temperature | Not shown. No driverless, admin-free path exists on JOES-PC (ACPI thermal zone is access denied and is not a core temperature anyway). GPU temperature only, from NVML. Running HWiNFO64 resident or shipping an MSR driver were declined. |
| Cadence | Sample every 10 s inside the source; repaint on the minute as today. The daemon's wake schedule does not change. |
| Weather location | Leeds. Coordinates go in the layout file (53.8008, -1.5491), not in secrets. |
| Tailscale / Apollo | Tailscale only, up or down. Apollo dropped as always-on and rarely worth a glance. |
| Approach | 1: a new `dial` component and an in-process `hardware` source. Not bars (2), not `nvidia-smi` through the `command` source (3, a process spawn per sample). |

## 2. Doctrine (design-doctrine gates)

**Gate 0, job statement for the new column:** *answer "is a drive about to fill", "what is the
machine doing right now", "do I need a coat" and "is the VPN up" in one glance, on a surface that
spends most of its life behind windows.* The owner widened the job from the v1 spec's two
questions to four; the controller flagged that a load dial cannot be read while a game covers it,
and the owner chose to proceed. Recorded, not re-opened.

**Gate 1:** high-frequency surface, decoration budget near zero. Dials are the one place the owner
has asked for ink beyond the value itself; they get a single thin arc each and nothing else. No
glow, no gradient, no bezel, no tick marks, no needle.

**Gate 3, one falsifiable claim per element:**

| Element | Without this, the user cannot ... |
|---|---|
| Temperature number | know whether to take a coat |
| Weather icon | tell rain from cloud without reading a number |
| CPU dial | see that something is chewing the CPU while they are away |
| GPU dial | see that a game or encode is still running the GPU |
| RAM dial | see that memory is nearly full |
| GPU temperature dial | see the GPU is running hot |
| Tailscale line | know the VPN is down before an Apollo session fails to connect |
| Clock, drives | unchanged from the v1 spec; the clock stays by the owner's earlier ruling |

Deleted under Gate 3 before drawing: wind speed, apparent temperature, precipitation amount (all
in the Open-Meteo response and all unused), GPU memory dial (GPU load already answers "is it
running"), instantaneous CPU and GPU readings (published for anyone who wants them, drawn
nowhere), Tailscale IP and peer count, Apollo entirely, dial captions ("CPU", "GPU", "RAM"),
replaced by the value drawn inside the arc.

**Gate 4, fact tally:** each fact appears once. The number inside a dial and the arc are one fact
in two encodings, which is the point of a dial and is allowed for exactly the four dials.

**Gate 5, costume:** a dial is an instrument register. It is earned only by a value that changes
continuously and has a natural full-scale (0..100 % load, 0..100 °C). The four dials meet that;
nothing else on the column may use one.

**Gate 7, states:** the layout must be rendered and looked at with: no GPU (NVML absent, four dials
become three plus a blank), fewer than 30 samples (fresh start), weather fetch failed (icon plate
and no number), Tailscale stopped, and the longest real weather string is not a concern because
none is drawn.

**Gate 7a:** location was asked as a town and resolved by the controller; nobody types a latitude.

## 3. `hardware` source

Type `hardware`. Settings: `every` (default 60 s), `sample` (default 10 s), `window` (default
300 s). Ring size is `window / sample`, at least 1.

**Sampler.** A `System.Threading.Timer` inside the source, started on the first `RefreshAsync`
and disposed with the source, fires every `sample` seconds and appends one reading per metric to a
fixed ring (`RollingWindow`: `Add`, `Average`, `Count`, no allocation after construction). A
failed reading is skipped, not recorded. All access is under one lock; the timer callback does
no I/O and no allocation beyond what the readers need.

The daemon's `Scheduler` sees only `every`; the source is due once a minute like `time`, so the
daemon still wakes once a minute. This is the "sample 10 s, paint on the minute" decision.

**Readers** (interface `IHardwareReader`, one native implementation, tests inject a fake):

- CPU: `GetSystemTimes` idle/kernel/user; load = 1 - Δidle / Δ(kernel+user), needs two readings
  so the first sample of a fresh process contributes nothing.
- RAM: `GlobalMemoryStatusEx`; used fraction and GB.
- GPU: NVML (`nvml.dll`, shipped by the NVIDIA driver in `System32`; probe
  `%ProgramFiles%\NVIDIA Corporation\NVSMI\` as a fallback). `nvmlInit_v2`,
  `nvmlDeviceGetHandleByIndex_v2(0)`, `nvmlDeviceGetUtilizationRates`, `nvmlDeviceGetMemoryInfo`,
  `nvmlDeviceGetTemperature(NVML_TEMPERATURE_GPU)`. Loaded with `NativeLibrary.TryLoad` and
  called through unmanaged function pointers, so a machine without the library, or with a
  non-NVIDIA GPU, gets `null` and no exception. Native AOT safe, no marshalling.

**Published** (all `NumberValue` unless said):

| Field | Meaning |
|---|---|
| `cpu` | 0..1 average load over the window |
| `cpuPct` | the same, 0..100 rounded to an integer, for text |
| `cpuNow` | latest single reading, 0..1 |
| `ram`, `ramPct` | used fraction over the window |
| `ramUsedGB`, `ramTotalGB` | latest reading, one decimal |
| `gpu`, `gpuPct`, `gpuNow` | GPU utilisation; absent when there is no GPU reader |
| `gpuMemory` | GPU memory used fraction, average; absent likewise |
| `gpuTempC` | average temperature, integer °C; absent likewise |
| `gpuTempFraction` | `gpuTempC / 100`, so a dial can bind it without arithmetic |
| `samples` | readings in the window right now (0..30 by default) |
| `window` | window length in seconds |

Fractions are published rounded to 3 decimals so content keys are stable across reads that differ
below a pixel (the same rule as `ResolvedBar`).

**Failure (spec 3.2):** a refresh with zero samples publishes `samples: 0` and no averages, so
every bound property falls back to its default. Nothing throws out of `RefreshAsync`.

**Cost:** the sampler is roughly four native calls every 10 s. `deskwall tick --measure` is
unaffected by design; the resident log and a four-minute external sample (the same measurement
the budget suite makes) must show idle CPU still at zero to the 15.6 ms quantum with the source
active. If it does not, that is a finding for `docs/architecture.md`, not a reason to stop
sampling.

## 4. `dial` component

`DialDef : ComponentDef`, JSON type `"dial"`.

| Property | Default | Notes |
|---|---|---|
| `fraction` | required | 0..1, clamped |
| `track` | `"#46FFFFFF"` | full-sweep arc under the fill |
| `fill` | `"#EBFFFFFF"` | the value arc |
| `threshold` | `1` | at or above this fraction the fill uses `thresholdFill` |
| `thresholdFill` | `"#D13438"` | same semantics as `bar` |
| `thickness` | `6` | stroke width in pixels |
| `startAngle` | `135` | degrees, clockwise from 12 o'clock, where the sweep begins |
| `sweep` | `270` | degrees of arc for fraction 1 |

The arc is centred in `rect`, radius `min(w, h) / 2 - thickness / 2`, flat caps. Fill sweeps
`sweep * fraction` degrees clockwise from `startAngle`. Nothing else is drawn: a number inside the
dial is an ordinary `text` component the layout places over it.

**Resolve:** `ResolvedDial(Id, Rect, Z, Fraction, Track, Fill, Thickness, StartAngle, Sweep)`;
content key parts: fraction at 3 decimals, both colours, thickness, both angles. `PaintBounds` is
`Rect` (the stroke is inside it by construction).

**Render:** `Surface.DrawArc(Rect, startDeg, sweepDeg, thickness, Color)` via
`ID2D1PathGeometry` + `ID2D1GeometrySink` (`BeginFigure` hollow, `AddArc`, `EndFigure` open,
`DrawGeometry`). A sweep of 360 or more is drawn as two arcs. New CsWin32 declarations:
`ID2D1PathGeometry`, `ID2D1GeometrySink`, `D2D1_ARC_SEGMENT`, `D2D1_SWEEP_DIRECTION`,
`D2D1_ARC_SIZE`, `D2D1_FIGURE_BEGIN`, `D2D1_FIGURE_END`. Verified shapes go into the spike
results doc like every other interop entry.

**Designer:** `PropertySchema` gains `DialProps`; `LayersPanel` names it `dial`; wherever the
designer offers "add component" the type appears with a sensible default rect (80x80). The
preview renders through Core, so nothing else changes.

**Golden:** `dial-states.png`: fractions 0, 0.5, 1, one over threshold, one with a different
start/sweep, one 12 px thick.

## 5. Weather recipe

An `http` source on the existing machinery, no code:

```json
{ "name": "weather", "type": "http", "every": 900, "settings": {
    "url": "https://api.open-meteo.com/v1/forecast?latitude=53.8008&longitude=-1.5491&current=temperature_2m,weather_code,is_day&timezone=Europe%2FLondon" } }
```

Response (measured 2026-09-21 08:00): `current.temperature_2m` 11.5, `current.weather_code` 2,
`current.is_day` 1. Open-Meteo needs no key and asks for at most one request per source
interval; 15 minutes is well inside its fair-use terms.

Components: `text` bound to `weather.json.current.temperature_2m | "{0:N0}°"`, and `image` bound
to `weather.json.current.weather_code | "runtime:assets/weather/{0}.png"`. The icon set is a
folder of PNGs named by WMO code (0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 71,
73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99), 96x96, white on transparent, from an MIT-licensed set
(Basmilius "Meteocons" preferred) with the licence file beside them and the mapping table in
`assets/weather/README.md`. Day/night variants are out of scope: a composite format takes one
value, and `is_day` would need a second.

**`runtime:` paths.** Today an `image.source` that is not a URL is opened as given, so a relative
path would resolve against the process's working directory and a committed starter cannot name a
per-user absolute path. `LayoutResolver` therefore expands the prefix `runtime:` on an image
source to `Paths.InRuntime(rest)` (forward or back slashes), after the binding has been formatted,
so `runtime:assets/weather/{0}.png` is legal. Braces are not used for the token because the
composite format would swallow them. Documented in `docs/layout-format.md` under `image`; a
resolver test covers it.

The repo keeps the icons at `assets/weather/`; the designer project links them into its output
like the starter layouts, and the designer copies `assets/weather` to
`%LOCALAPPDATA%\DeskWall\assets\weather` whenever it copies a starter (first run or starter picker),
overwriting nothing that is already there. For the owner's machine the controller copies the
folder once when registering the layout.

## 6. Tailscale recipe

A `command` source, no code:

```json
{ "name": "tailscale", "type": "command", "every": 300, "settings": {
    "command": "C:\\Program Files\\Tailscale\\tailscale.exe", "args": "status --json", "timeout": 10 } }
```

`json.BackendState` is `Running` when up, `Stopped` / `NeedsLogin` / `NoState` otherwise
(measured: `Running`). One `text` component, size 13, bound to
`tailscale.json.BackendState | "VPN {0}"`. Bindings have no conditionals, so the line is present
when healthy; it is the smallest text on the column.

## 7. Starter layout `layouts/column-system.json`

Right-hand column, x 3220..3392 (172 wide), same base image and encode settings as
`clock-disks.json`. Sources: `time`, `disks`, `weather`, `hardware`, `tailscale`. Components:

| id | type | rect | binding |
|---|---|---|---|
| clock | text | 3220,40,172,78 | as `clock-disks.json` |
| temp | text | 3220,128,108,48 | `weather...temperature_2m | "{0:N0}°"`, size 40, weight 300, right |
| sky | image | 3336,128,56,56 | weather icon path, fit contain |
| vpn | text | 3220,190,172,20 | `tailscale.json.BackendState | "VPN {0}"`, size 13 |
| cpuDial / cpuPct | dial + text | 3220,1000,80,80 | `hardware.cpu`, threshold 0.9; text `hardware.cpuPct | "{0}%"` centred |
| gpuDial / gpuPct | dial + text | 3312,1000,80,80 | `hardware.gpu`, threshold 0.9 |
| ramDial / ramPct | dial + text | 3220,1096,80,80 | `hardware.ram`, threshold 0.9 |
| tempDial / tempC | dial + text | 3312,1096,80,80 | `hardware.gpuTempFraction`, threshold 0.83 (83 °C, the 3050's throttle point); text `hardware.gpuTempC | "{0}°"` |
| drives | repeater | 3220,1260,172,92 | as `clock-disks.json` |

The two blocks (weather + VPN at the top under the clock, dials just above the drives) leave the
middle of the column empty for the Steam covers the `steam-recent.json` recipe puts there; a
combined recipe is a layout-file edit for whoever wants it, not another starter now.

Registered live for the owner's display when merged (he asked to see it), alongside the existing
`clock-disks.json` copy which stays as the fallback.

## 8. Documentation

- `docs/sources.md`: a `hardware` section in the same shape as the others.
- `docs/layout-format.md`: a `dial` section and the recipe bindings as examples 11 and 12.
- `layouts/README.md`: the new starter, the weather icon folder note, the Tailscale requirement.
- `docs/architecture.md`: the sampler timer under the process model and the measured idle cost
  with the source active.
- `docs/superpowers/plans/2026-09-20-phase1-spike-results.md`: the new CsWin32 shapes.
- `assets/weather/README.md`: licence, source, WMO mapping.

## 9. Testing

- `RollingWindowTests`: average of N, wrap-around, empty.
- `HardwareSourceTests`: fake reader, fake clock; CPU load from two readings; no GPU means no GPU
  fields; `samples` counts; rounding to 3 decimals; zero samples publishes only `samples`.
- `DialResolveTests`: defaults, threshold switch, key parts change with fraction, clamping.
- `GoldenTests`: `dial-states.png`.
- `LayoutFileTests` (existing pattern): `column-system.json` loads and every binding parses.
- Designer: `PropertySchemaTests` already asserts every def type has a non-empty schema, so a
  missing `DialProps` fails the suite.
- Live: after merge, the layout registered on JOES-PC, one screenshot of the column with real
  values, and the Gate 7 states rendered by pointing `deskwall tick --layout` at a copy with the
  source removed (no GPU) and the weather URL broken.
- Cost: four-minute resident sample with the new layout, the same numbers the budget suite prints,
  recorded in `docs/architecture.md`.

## 10. Out of scope, carried forward

- CPU temperature (needs a kernel driver or a resident monitor).
- Day/night weather icons and a `map`/lookup binding operator.
- Conditional visibility (hide the VPN line when healthy).
- Apollo streaming state.
- A combined starter with Steam covers and the new widgets.
