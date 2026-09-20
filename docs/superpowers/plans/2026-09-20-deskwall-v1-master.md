# DeskWall v1 Master Plan

> **For agentic workers:** This is the phase graph and execution strategy. Each phase has its
> own task-level plan in this folder (`...-phaseN-<name>.md`). Execute a phase plan with
> superpowers:subagent-driven-development. Do not start a phase whose prerequisites in the
> graph below are not merged into `v1`.

**Goal:** Replace the PowerShell POC with a native-AOT daemon, a sources/components model,
and a WPF designer, meeting the cost budget in the spec.

**Architecture:** `DeskWall.Core` (AOT-safe library) holds every piece of logic. `deskwall.exe`
hosts it as a resident daemon with a hidden window and tray icon. `DeskWall.Designer.exe`
edits layout JSON and never talks to the daemon except through files.

**Tech Stack:** C# / .NET 10 LTS, native AOT, CsWin32 (Direct2D, DirectWrite, WIC, shell COM),
System.Text.Json source generation, WPF, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md`

## Global Constraints

Copied from the spec. Every task in every phase plan inherits these.

- .NET 10 LTS. `DeskWall.Core` and `DeskWall.Daemon` must publish with `PublishAot=true`
  and zero AOT or trim warnings (`TreatWarningsAsErrors` for IL2xxx/IL3xxx).
- No GPU: no D3D device is ever created. Direct2D renders to a WIC bitmap in software.
- No third-party native dependencies. NuGet allowed: `Microsoft.Windows.CsWin32` (build
  only), xUnit and its runners. Anything else needs a line in this file first.
- Budget (spec 1.2): idle private working set 10 MB after trim; idle CPU 0; clock-only tick
  60 ms wall / 40 ms CPU; cold start to first wallpaper 500 ms; under 100 handles and under
  5 threads at idle. Measured with `deskwall tick --measure` on JOES-PC.
- `.ps1`/`.cs` sources are ASCII only (the POC rule stays; it costs nothing).
- Runtime state lives in `%LOCALAPPDATA%\DeskWall`. Never in the repo. Secrets only in
  `secrets.json` there.
- Design doctrine gates apply to every starter layout and to the designer UI.
- Commits end with the attribution lines the session provides.

## Branch and worktree layout

```
poc            existing branch, frozen after Phase 1 Task 0 lands
v1             integration branch, created from poc; every lane merges here
v1/<lane>      one branch per parallel lane, checked out in ../DeskWall-<lane>
```

Create a lane: `git worktree add ../DeskWall-<lane> -b v1/<lane> v1` (or the Agent tool's
`isolation: "worktree"`, which does the same). Finish a lane: rebase onto `v1`, run
`dotnet test`, run `deskwall tick --measure` if the render path was touched, fast-forward
merge into `v1`, remove the worktree. Never merge a lane that widens the budget.

Interfaces are frozen at the start of each fan-out (the "seam" task in each phase). A lane
that needs to change a frozen interface stops and raises it; the seam owner changes it in
`v1` and every lane rebases.

## Phase graph

```
P1 Core + tick ─────┬──> P2 Daemon ─────────┐
                    ├──> P3 Shortcuts ──────┤
                    ├──> P4 Sources (x5) ───┼──> P5 Designer ──> P6 Verify, budgets, docs
                    └────────────────────────┘
```

P2, P3 and P4 are independent of each other and only need P1 merged. P5 needs P2 through P4
because it configures and previews all of them. P6 closes the parity gate.

| Phase | Plan file | Depends on | Lanes | Est. tasks |
|---|---|---|---|---|
| P1 Core model, renderer, `tick` | `...-phase1-core.md` | none | 3 after the seam | 12 |
| P2 Daemon: window, scheduler, tray, hot reload, install | `...-phase2-daemon.md` | P1 | 2 | 8 |
| P3 Shortcut manager, `calibrate` | `...-phase3-shortcuts.md` | P1 | 1 | 6 |
| P4 Sources: http, rss, file, command, system; image cache; secrets | `...-phase4-sources.md` | P1 | 5 | 8 |
| P5 Designer | `...-phase5-designer.md` | P2, P3, P4 | 3 | 12 |
| P6 verify, goldens, budget tests, starter layouts, docs, POC removal | `...-phase6-parity.md` | P5 | 3 | 8 |

Phase plans P2 through P6 are written when their phase starts, against the interfaces P1
actually shipped. Each is written with superpowers:writing-plans and reviewed before
execution.

## Agents and models

Three roles. Pick by the shape of the task, not by phase.

| Role | Model | Use for |
|---|---|---|
| **Seam owner / integrator** | Fable (this session, or `subagent_type: "fork"`) | Freezing interfaces, anything touching CsWin32 COM or AOT, the renderer, the scheduler wake logic, the designer canvas, merging lanes, all reviews. Work where a wrong assumption costs a day. |
| **Lane worker** | Sonnet (`model: "sonnet"`, `general-purpose`, `isolation: "worktree"`) | Leaf tasks behind a frozen interface with a test to hit: an `ISource` implementation, a JSON converter, a parser, a settings page, docs, starter layouts, golden-test scaffolding. |
| **Escalation** | Opus (`model: "opus"`) | A lane-worker task that Sonnet has failed twice, or a lane whose tests need real judgement about the domain (VDF quirks, RSS edge cases). Cheaper than pulling Fable off integration. |

Rules:

- **Reviews are always Fable.** Two-stage per subagent-driven-development: spec compliance,
  then code quality. A Sonnet lane never self-approves.
- **One worktree per lane, one lane per agent.** No two agents in the same worktree.
- **Fan-out only after the seam task is merged.** Before that, one agent, sequential.
- **Budget gate is mechanical.** Any lane touching Core render or Daemon wake code runs
  `deskwall tick --measure` and pastes the per-stage table into its report. A reviewer who
  does not see the table rejects.
- Haiku is not used. Nothing here is cheap enough to be worth the retries.

### Per-phase lane assignment

**P1** (detailed in the phase plan): Fable does Tasks 0 to 2 sequentially (scaffold, spike,
value model = the seam). Then three lanes: `v1/p1-binding` (Sonnet: parser, layout JSON),
`v1/p1-sources` (Sonnet: time, disks, display signature), `v1/p1-render` (Fable: Surface,
renderer, wallpaper apply). Fable integrates: resolver, `tick`, incremental redraw.

**P2**: `v1/p2-host` (Fable: hidden window, message pump, waitable timer, tray, trim) and
`v1/p2-plumbing` (Sonnet: `Scheduler` next-wake maths with fake clock, rolling log, layout
store watcher, `install`/`uninstall` Run key and wallpaper restore). Fable wires them.

**P3**: one lane `v1/p3-shortcuts` (Fable: port `DeskIcons.cs` to CsWin32, folder flags,
slot files, ICO, `calibrate`). Sonnet may take the ICO writer and the `.lnk` writer as
sub-tasks inside the lane if Fable hands them off with tests.

**P4**: five lanes, one per source, all Sonnet, all against the frozen `ISource`:
`v1/p4-http` (plus the remote image cache and secrets reference, since http needs both),
`v1/p4-rss`, `v1/p4-file`, `v1/p4-command`, `v1/p4-system`. Fable reviews each and merges in
the order they finish. Escalate rss or command to Opus if parsing edge cases stall.

**P5**: `v1/p5-canvas` (Fable: WPF canvas, drag, resize, snap, undo, live Core render),
`v1/p5-panels` (Sonnet: sources panel, properties panel with binding picker, z-list),
`v1/p5-settings` (Sonnet: settings page, secrets editor, footprint panel, first-run starter
picker). Fable integrates and applies the design doctrine gates.

**P6**: `v1/p6-verify` (Fable: screenshot, pixel diff, arrow padding report, clock crop),
`v1/p6-tests` (Sonnet: golden images, budget test harness, `.gitignore` scoping),
`v1/p6-docs` (Sonnet: layout format reference, source reference, starter layouts, README
rewrite, `poc/` removal after the parity gate).

## Parity gate (end of P6)

All true on JOES-PC before `poc/` is deleted and the `DeskWall Tick` task removed:

- [ ] Starter layout reproduces the current right-hand column; `deskwall verify` passes
      (5/5 arrow padding on every cover).
- [ ] Every line of the budget table passes via the budget tests.
- [ ] Manual acceptance list in spec section 9 walked and ticked in the P6 plan.
- [ ] Daemon has run for 24 hours with zero logged tick failures.
