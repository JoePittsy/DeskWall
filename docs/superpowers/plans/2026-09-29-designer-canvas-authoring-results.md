# Designer canvas authoring: Phase 1 results (Task 1.6)

Measured 2026-09-29 by the integrator, after `lane/p1-expander` and `lane/p1-daemon` merged.

## Machine

- **JOES-XPS-17**, not JOES-PC. The 10 MB budget is defined on JOES-PC, so these numbers are a
  before/after comparison on one machine, not a reading against that budget.
- Display: one primary monitor at 1920x1200, 100% scaling (`SHP1517`). `column-system.json` is
  authored for 3440x1440, so every tick also runs `LayoutScaler`.
- MSVC linker present; both builds are real native-AOT publishes.
- The owner's live daemon (PID 38968, `%LOCALAPPDATA%\Programs\DeskWall\deskwall.exe`) ran
  throughout and painted every minute. Nothing here touched it or `%LOCALAPPDATA%\DeskWall`.

## Builds

`dotnet publish src/DeskWall.Daemon -c Release -r win-x64`, with the VS Installer PATH fix:

| Build | Commit | Warnings |
|---|---|---|
| before | e437597 (a `git worktree` in `%TEMP%`, removed afterwards) | 0 |
| after | `main` at edeab40 (the Phase 1 merges plus the un-skipped tests) | 0 |

## Method

Every column has its own scratch home. It runs
`tick --layout <file> [--force] --measure --no-apply --no-shortcuts` through
`Start-Process -Wait`, so no two ticks ever overlap.

- **before-v1**: the before exe ticks `layouts\column-system.json` (v1).
- **after-v1**: the after exe ticks the same v1 file.
- **after-v2**: the after exe ticks that file's `deskwall migrate` output (v2, 8 copies, no loose
  components, no overrides).

The ticks are interleaved round-robin: one tick per column, then the next round. `resolve` is
dominated by the network sources (weather over http, Tailscale). A first sequential run showed
the resolve of every column, the before build included, move between 250 ms and 1.4 s from one
minute to the next. Running the columns back to back would have charged that drift to whichever
column ran last.

Five forced rounds come first, then five unforced (skip-path) rounds, as the plan specifies. Two
more runs of 15 + 15 rounds follow as confirmation, because the per-tick spread of `total`
(313-473 ms) is ten times the 15.6 ms the pass rule resolves.

Medians are the upper-middle value for an even count. `cpu` is process CPU time, which Windows
counts in 15.6 ms quanta. `load` (read + expand + scale) is printed outside `total`, and the
before build does not print it.

## Results: the plan's run (5 + 5 rounds)

Forced ticks, all 22 components redrawn, median ms:

| Row | before-v1 | after-v1 | after-v2 | after-v1 vs before | after-v2 vs before |
|---|---|---|---|---|---|
| resolve | 257 | 245 | 257 | -12 | 0 |
| draw | 62 | 69 | 71 | +7 | +9 |
| total | 349 | 362 | 366 | +13 | **+17** |
| cpu | 141 | 141 | 156 | 0 | +15 (one quantum) |
| load | n/a | 7 | 12 | | +5 vs after-v1 |

Unforced ticks: 4 of the 5 in each column skipped (redrawn 0, draw 0, encode 0). The fifth
redrew the 2-3 live dial components. Medians over the skipped ticks only:

| Row | before-v1 | after-v1 | after-v2 |
|---|---|---|---|
| total | 257 | 247 | 248 |
| cpu | 47 | 47 | 94 (samples 16, 31, 94, 94) |

## Results: confirmation (15 + 15 rounds, twice)

| Row | before-v1 | after-v1 | after-v2 |
|---|---|---|---|
| forced total, run A | 336 | 337 | 330 |
| forced total, run B | 326 | 333 | 327 |
| forced cpu, run A | 125 | 125 | 125 |
| forced cpu, run B | 125 | 141 | 141 |
| skip-path total, run A | 252 | 243 | 251 |
| skip-path total, run B (7 / 4 / 6 ticks skipped) | 241 | 258 | 257 |
| skip-path cpu, run B | 47 | 47 | 62 |
| load, run B | n/a | 4 | 12 |

## Idle handles and threads

Each column ran a scratch `run --no-tray --no-shortcuts` with its layout registered through
`layouts set`, sampled after 30 s, then stopped with `deskwall --home <scratch> stop`. The
wallpaper was put back afterwards. Every log shows 0 ERROR lines.

| | before-v1 | after-v1 | after-v2 |
|---|---|---|---|
| handles | 461 | 463 | 462 |
| threads | 21 | 19 | 22 |
| private bytes | 30.0 MB | 30.4 MB | 30.6 MB |

Thread counts at 30 s are still settling, as runtime startup threads exit. The phase 1 spike
results saw 17 -> 10 on JOES-PC. They are recorded, not judged. The absolute counts are over the
`< 100 h / < 5 t` idle budget in `BudgetTests`, as they already were before this work (`OVER` in
`2026-09-20-phase1-spike-results.md`). This change does not move them.

## Verdicts against the Task 1.6 pass rule

| Rule | after-v1 | after-v2 |
|---|---|---|
| `total` within one 15.6 ms quantum of before | PASS (+13; +1 and +7 on confirmation) | **Over at n=5 (+17 ms, 1.4 ms past the quantum)**; PASS on both confirmations (-6, +1) |
| `cpu` within one quantum of before | PASS (0; 0 and +16) | PASS (+15; 0 and +16) |
| skip path unchanged | PASS: skips the same way; total within noise; cpu 47 vs 47 | PASS on confirmation: skips the same way, total within noise, cpu +15 (one quantum). At n=5 the skip cpu median read +47, from a bimodal 16/31/94/94 sample |
| at most 2 more handles | PASS (+2) | PASS (+1) |

**Overall: PASS**, with the n=5 excursion raised rather than rounded away. The plan's
five-sample run put after-v2's forced `total` 1.4 ms over the rule, and its skip-path `cpu` three
quanta over. Neither reproduced in 30 more interleaved samples per column. Nothing in v2 adds
work inside the tick: the components it resolves and draws are the ones v1 has, pixel for pixel.

The one real, repeatable cost is `load`: about +7 ms (v1 4-7 ms, v2 11-13 ms) to read the 15 widget
files and expand the 8 copies. It sits outside `total`. The resident daemon pays it once per
activation (a layout edit, a widget edit or a display change), never per minute. A one-shot
`tick` pays it every time.

## Identity (JIT, before the AOT runs)

`deskwall migrate --check` on a scratch copy of `column-system.json` reported `equivalent: yes`.
The real `migrate` wrote `column-system.v1.json`, hash-identical to the input, and a 1866-byte v2
file. Three forced ticks ran on one scratch home in the order v1, v2, v1:

- `frame.raw` was byte-identical across all three;
- the v2 tick's `keysById` was identical to the v1 tick after it (22 ids, same keys);
- the first v1 tick differed from both in the same three keys (`dial-3` RAM, `dial-4` GPU
  temperature), which is live drift between reads, not the migration.

An earlier pair on two separate homes differed in `weather-1.sky` as well, because the key holds
the `runtime:` path, which resolves into each home.

## D3, the runtime proof

The native-AOT `deskwall.exe` from `main` ticked the v2 `column-system.json` 100 times across the runs
above (the two sequential runs, the plan's run and both confirmations). Every tick exited 0, and
every forced tick drew 22 components. It also ran resident on the file for 30 s with no ERROR in
its log. Reading and expanding a v2 file therefore works under native AOT, not only under the
analyzers.
