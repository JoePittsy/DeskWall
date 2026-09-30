# 1 October: finish the lanes, merge, ship

Repo `D:\Source\Personal\DeskWall`, `main` at the loud layout (live on the desktop since 19:57 on
30 Sept). Read `CLAUDE.md`. Seven lane branches exist, each in a sibling worktree
(`DeskWall-<name>`), all built overnight by Copilot agents; three were cut off by the quota and
their trees were banked as a final `WIP:` commit (not built, not reviewed). Logs with `--resume`
ids are in `.superpowers/copilot-*.log`.

| Branch | Worktree | State |
|---|---|---|
| `spike/harden` | (worktree removed) | **8 fixes merged into `main` and published 30 Sept 23:20** (`f4c88bf`); its WIP commit (one new test file, `SurfaceTests.cs`) is still unmerged; no report written |
| `spike/presence-sources` | `DeskWall-presence` | done, reported |
| `spike/hearth-feed` | `DeskWall-hearth` | done, reported |
| `spike/photo-phase` | `DeskWall-photo` | done, reported |
| `spike/parity-gate` | `DeskWall-parity` | done; **last commit `04ef104` deletes `poc/`: do NOT merge it until the JOES-PC runbook passes** |
| `spike/designer-catchup` | `DeskWall-designer` | 4 commits + WIP; critique round not started |
| `spike/multi-monitor` | `DeskWall-monitors` | 8 commits + WIP; no report |

## 1. Finish the three cut-off lanes (one run each, from their worktree)
`copilot --resume=<id from the log>` with: "You were cut off by quota. Your last commit is a
WIP bank of your working tree: build it, run the tests, fix what is broken, split or amend it
into proper commits, finish the brief's remaining items, write the lane report." Designer's
remaining item is the critique-4 round; monitors' is the report and the gallery shots; harden's
is `docs/superpowers/plans/2026-09-30-harden-report.md`.

## 2. Merge, in this order, on `main`
**Already merged and published on the night of 30 Sept: harden (8 fixes), presence, hearth,
photo, parity (without the POC deletion), all with both suites green. Their worktrees are
removed; `spike/parity-gate`'s final commit `04ef104` (delete `poc/`) is still unmerged on
purpose.** Remaining: designer, then monitors (both carry a `WIP:` bank). After each: `dotnet build` clean, full tests green, resolve
conflicts by reading both sides (the duplicate `time.phase` between presence and photo is
expected: keep presence's, delete photo's copy). Then `scripts/gallery.ps1` and **look at every
PNG**; `deskwall tick --repeat 10 --measure` on `layouts/alpine-vision.json` and on
`alpine-vision-photos.json`; publish (`scripts/publish.ps1 -Aot`, VS Installer on PATH, designer
closed); confirm a clean `LayoutChanged` tick in `deskwall.log`. Remove the worktrees
(`git worktree remove`), keep the branches until the owner deletes them.

## 3. Dial back (the loud report's list, in its order)
Quantise the sky Blend input to 15-minute steps; halve the dawn/dusk band alpha; drive wash from
0.92 or cap 35 %; cap CPU foothills height and area; rain 600 -> 350; leave the sun/moon apex
(owner's call). Re-run the gallery after each and stop when it looks like something you would
leave behind windows for eight hours. Apply live.

## 4. On JOES-PC (owner, or an agent there)
`docs/superpowers/plans/2026-09-30-lane-parity-*` runbook: real `deskwall verify`, disable the
POC task, then merge `04ef104`. Build Hearth from
`docs/superpowers/plans/2026-09-30-hearth-feed-spec.md`. Install with `-Aot`.
