# 1 October: finish the lanes, merge, ship

Repo `D:\Source\Personal\DeskWall`, `main` at the loud layout (live on the desktop since 19:57 on
30 Sept). Read `CLAUDE.md`. Seven lane branches exist, each in a sibling worktree
(`DeskWall-<name>`), all built overnight by Copilot agents; three were cut off by the quota and
their trees were banked as a final `WIP:` commit (not built, not reviewed). Logs with `--resume`
ids are in `.superpowers/copilot-*.log`.

| Branch | Worktree | State |
|---|---|---|
| `spike/harden` | (worktree removed) | **8 fixes merged into `main` and published 30 Sept 23:20** (`f4c88bf`); WIP (`SurfaceTests.cs`) and report -- #51 |
| `spike/presence-sources` | `DeskWall-presence` | done, reported |
| `spike/hearth-feed` | `DeskWall-hearth` | done, reported |
| `spike/photo-phase` | `DeskWall-photo` | done, reported |
| `spike/parity-gate` | `DeskWall-parity` | done; **last commit `04ef104` deletes `poc/`: not before the JOES-PC runbook -- #13** |
| `spike/designer-catchup` | `DeskWall-designer` | 4 commits + WIP -- #29 |
| `spike/multi-monitor` | `DeskWall-monitors` | 8 commits + WIP -- #30 |

## 1. Finish the three cut-off lanes (one run each, from their worktree)
`copilot --resume=<id from the log>` with: "You were cut off by quota. Your last commit is a
WIP bank of your working tree: build it, run the tests, fix what is broken, split or amend it
into proper commits, finish the brief's remaining items, write the lane report." Designer #29,
monitors #30, harden #51.

## 2. Merge, in this order, on `main`
**Already merged and published on the night of 30 Sept: harden (8 fixes), presence, hearth,
photo, parity (without the POC deletion), all with both suites green. Their worktrees are
removed; `spike/parity-gate`'s final commit `04ef104` (delete `poc/`) is still unmerged on
purpose.** Remaining: designer (#29), then monitors (#30) (both carry a `WIP:` bank). After each: `dotnet build` clean, full tests green, resolve
conflicts by reading both sides (the duplicate `time.phase` between presence and photo is
expected: keep presence's, delete photo's copy). Then `scripts/gallery.ps1` and **look at every
PNG**; `deskwall tick --repeat 10 --measure` on `layouts/alpine-vision.json` and on
`alpine-vision-photos.json`; publish (`scripts/publish.ps1 -Aot`, VS Installer on PATH, designer
closed); confirm a clean `LayoutChanged` tick in `deskwall.log`. Remove the worktrees
(`git worktree remove`), keep the branches until the owner deletes them.

## 3. Dial back (the loud report's list) -- #53

## 4. On JOES-PC (owner, or an agent there)
Parity runbook: real `deskwall verify` (#2), retire the POC task (#12), then merge `04ef104`
(#13). Build Hearth (#35). Install with `-Aot`.
