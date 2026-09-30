# DeskWall v1 Phase 6: Verify, Budgets, Docs and Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the parity gate in the master plan: `deskwall verify` proves icon placement by
pixel diff, golden-image tests pin the renderer, budget tests assert the spec's cost table under
native AOT, the docs describe the shipped system, and the PowerShell POC is retired.

**Architecture:** `verify` is the POC's `verify.ps1` grown up, built on Phase 3's `Screenshot` and
`Calibrator.DiffBounds`. Golden tests render fixed layouts with fixed fonts through the real
Direct2D path and compare to checked-in PNGs. Budget tests drive `deskwall tick --measure` and the
running daemon's `Footprint` on the reference machine and are skipped elsewhere by a trait.

**Tech Stack:** as before. Fonts for goldens: `Segoe UI` and `Segoe UI Light` are Windows-shipped,
so no font files are checked in; goldens are rendered and compared on Windows only.

**Spec:** `docs/superpowers/specs/2026-09-20-deskwall-v1-design.md` (sections 9, 1.2, 10)
**Master plan:** `docs/superpowers/plans/2026-09-20-deskwall-v1-master.md` (parity gate)
**Depends on:** Phases 2 to 5 merged; the MSVC linker installed so `dotnet publish` native AOT works
(budget tests are meaningless without it).

## Global Constraints

- Everything in the master plan's Global Constraints.
- Budget tests carry `[Trait("Category", "Budget")]` and are excluded from the default `dotnet test`
  run (`tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj` gets a default filter, or the
  runsettings excludes the trait); they run explicitly with
  `dotnet test --filter Category=Budget` against a published AOT `deskwall.exe`.
- Golden PNGs live under `tests/DeskWall.Core.Tests/Goldens/` and are tracked (root `.gitignore`
  only excludes `/*.png`). A golden changes only in a commit whose message says why.
- `verify` and `calibrate` mutate the live desktop for seconds and restore it; they are commands,
  never on the tick path.
- The POC is deleted only after every parity gate item is ticked in this plan, in its own commit.
- Lane assignment: `lane/p6-verify` (Opus: Tasks 1, 2), `lane/p6-tests` (Sonnet: Tasks 3, 4),
  `lane/p6-docs` (Sonnet: Tasks 5, 6); Task 7 controller.

## Interfaces consumed

```csharp
Screenshot.Capture(Rect) : Surface;  Calibrator.DiffBounds(shot, reference, probe, threshold) : Rect?   // Phase 3
ShortcutManager, ShortcutPlan.IconPosition, Calibration.Load(), DesktopView.GetPosition               // Phase 3
TickRunner(..., shortcuts, images).RunAsync; TickTimings.ToTable(); FrameState                        // Phases 1-4
LayoutStore.Default().Resolve(sig); Monitors.Enumerate()                                              // Phase 2
Footprint.Current(); RollingLog.Default()                                                              // Phase 2
Surface.Load/GetPixel; FrameRenderer.RenderAll; BaseCache.Ensure; LayoutResolver.Resolve             // Phase 1
```

---

### Task 1: `deskwall verify` (lane `lane/p6-verify`, Opus)

**Files:**
- Create: `src/DeskWall.Core/Verify/Verifier.cs`, `src/DeskWall.Core/Verify/VerifyReport.cs`
- Modify: `src/DeskWall.Daemon/Program.cs` (`verify [--pad N] [--threshold N] [--json]`)
- Test: `tests/DeskWall.Core.Tests/Verify/VerifierTests.cs` (pure part on synthetic surfaces)

**Interfaces:**
- Produces:

```csharp
public sealed record SlotCheck(int Slot, string Id, Rect Cover, (int X, int Y) Wanted, (int X, int Y)? Got, Rect? ArrowBox, int LeftPad, int BottomPad, bool Ok, string? Note);
public sealed record VerifyReport(DisplaySignature Signature, IReadOnlyList<SlotCheck> Slots, string ScreenshotPath, string ClockCropPath, bool Ok)
{ public string ToText(); public string ToJson(); }

public static class Verifier
{
    /// <summary>Pure: for one cover rect, find the arrow box in the screenshot vs the composed frame
    /// (inset 2 px to dodge JPEG ringing) and compute the left and bottom padding.</summary>
    public static SlotCheck CheckSlot(Surface shot, Surface composed, ResolvedShortcut s, (int X, int Y) wanted, (int X, int Y)? got, int wantPad, int threshold);
    /// <summary>Live: minimise all, screenshot the primary monitor, load the composed frame from
    /// frame.raw, check every ResolvedShortcut of the current layout, save the right-hand 400 px
    /// column and an 8x clock crop (if a text component named clock exists) to the runtime dir,
    /// undo minimise, return the report. Exit code semantics: Ok = every slot found at wantPad.</summary>
    public static VerifyReport Run(int wantPad = 5, int threshold = 60, Action<string>? log = null);
}
```

- [x] **Step 1: Failing tests** (`CheckSlot` on synthetic 200x300 surfaces: arrow at exact pad -> Ok, LeftPad 5, BottomPad 5; arrow 1 px off -> not Ok with pads 6/5; no diff -> Ok false with Note "NO ICON FOUND"; JPEG-noise blip below threshold ignored).
  *Evidence:* `tests/DeskWall.Core.Tests/Verify/VerifierTests.cs` (2fde8d1; box-size check a034712;
  `FindClock` cases d212413). 2026-09-30: `dotnet test --filter FullyQualifiedName~VerifierTests`
  19 passed.
- [x] **Step 2: Implement.** `Run` resolves the layout via `LayoutStore.Default().Resolve(primary.Signature)`, resolves components with the current source values (`TickRunner`'s registry is not available: build a fresh `SourceRegistry`, refresh every source once with a 5 s timeout), takes `LastShortcuts`-equivalent from the resolved list, computes `wanted` via `ShortcutPlan.IconPosition` with `Calibration.Load()`, reads `got` via `DesktopView.GetPosition`, uses `MinimizeAll`/`UndoMinimizeALL` as `Calibrator` does, `Screenshot.Capture(primary.Bounds)`, `Surface.LoadRaw(frame.raw)`.
  *Evidence:* 2fde8d1; 413938b (MinimizeAll never ran: fixed); d212413 finished it on
  2026-09-30. Two gaps closed there: the clock crop was never written for v2 layouts (the clock
  is `clock-1.clock`, verify looked for exactly `clock`), and hidden desktop icons read as
  "NO ICON FOUND" on every slot instead of saying why. Exit codes as shipped: 0 OK, 4 any slot
  failed or no shortcut components (no false green), 1 cannot run (no `frame.raw`, frame not the
  monitor's size, icons hidden). Live on JOES-XPS-17, scratch home, 3440x1440: `clock-now.png`,
  `verify-desktop.png` and `verify-log.txt` written; `clock-disks.json` (no shortcuts) exit 4;
  a scratch layout with one shortcut exit 1 "desktop icons are hidden" (this machine's folder
  view reports unavailable).
- [ ] **Step 3: Live run** on JOES-PC (console session, 3440x1440) with `layouts/steam-recent.json` active and the daemon running: expected `left pad 5, bottom pad 5` on every slot, `RESULT: OK`. Paste the text report into the lane report; keep `verify-desktop.png` and `clock-now.png` in the runtime dir.
  **TO RUN ON JOES-PC:** runbook R2 below.
- [x] **Step 4: Commit** (2fde8d1, same message; follow-ups a034712, 413938b, d212413, 13d4293) `git commit -m "verify: screenshot vs composed frame, arrow padding per slot, clock crop"`

---

### Task 2: Budget test harness (lane `lane/p6-verify`, Opus)

**Files:**
- Create: `tests/DeskWall.Core.Tests/Budget/BudgetTests.cs`, `tests/DeskWall.Core.Tests/Budget/DaemonProcess.cs`, `tests/deskwall.runsettings`
- Modify: `tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj` (`<RunSettingsFilePath>` pointing at the runsettings that excludes `Category=Budget` by default)

Each test finds the published AOT exe at `src/DeskWall.Daemon/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/deskwall.exe`
(skips with a clear message if absent), uses a scratch `DESKWALL_HOME` with `layouts/clock-disks.json`
registered for the current signature, and asserts the spec 1.2 table:

| Test | Method | Budget |
|---|---|---|
| `ColdStart_To_First_Wallpaper` | `Process.Start(exe, "run --no-tray")`, poll `deskwall.jpg` mtime at 10 ms | under 500 ms |
| `Idle_PrivateBytes_After_Trim` | daemon running 3 min, then `Process.PrivateMemorySize64` | under 10 MB |
| `Idle_Cpu_Between_Wakes` | `TotalProcessorTime` delta over a 4-minute window minus the ticks' own `cpu` from the log | under 50 ms total (measurement noise floor; the spec says 0) |
| `Idle_Handles_And_Threads` | `HandleCount` under 100, `Threads.Count` under 5 after 3 min | as spec |
| `ClockOnly_Tick_Wall_And_Cpu` | `deskwall tick --measure` (clock layout, warm cache) parsed table | total under 60 ms wall, cpu under 40 ms |

Log the actual numbers with `ITestOutputHelper` and write them to
`docs/superpowers/plans/2026-09-20-phase1-spike-results.md` under `## Phase 6 budget results`
(the harness prints a markdown row; the lane pastes it).

- [x] **Step 1:** runsettings excludes the trait; `dotnet test` count unchanged; `dotnet test --filter Category=Budget` runs five tests (skipped without the exe).
  *Evidence:* f275cba. 2026-09-30: plain `dotnet test` ran 518 Core + 591 Designer, no budget
  test among them; `--filter Category=Budget` ran exactly five.
- [x] **Step 2:** run on JOES-PC after `dotnet publish -c Release -r win-x64`; paste results. Any failing line is a finding for the controller, not something to loosen.
  *Evidence:* JOES-PC 2026-09-21 (826453c, d71d787) and JOES-XPS-17 2026-09-30 (c2be4c8), both
  in `2026-09-20-phase1-spike-results.md` "Phase 6 budget results". Three rows OK on both
  machines; `Idle_Handles_And_Threads` and `ClockOnly_Tick_Wall_And_Cpu` OVER on both. The
  2026-09-30 pass attributes them to the resident graphics stack and the Windows thread pool
  (not tunable by swapping pools: measured) and hands them to the owner as an architecture
  decision. See "Parity gate status" below.
- [x] **Step 3: Commit** (f275cba, same message) `git commit -m "Budget tests: AOT daemon footprint and tick cost against spec 1.2"`

Lane `lane/p6-verify` complete.

---

### Task 3: Golden-image tests (lane `lane/p6-tests`, Sonnet)

**Files:**
- Create: `tests/DeskWall.Core.Tests/Goldens/*.png`, `tests/DeskWall.Core.Tests/Goldens/GoldenTests.cs`, `tests/DeskWall.Core.Tests/Goldens/layouts/*.json`, `tests/DeskWall.Core.Tests/Goldens/Compare.cs`
- Modify: `tests/DeskWall.Core.Tests/DeskWall.Core.Tests.csproj` (copy Goldens to output)

Four goldens at 860x360 (a quarter-scale canvas so files stay small): `text-styles` (all four
effects, three sizes, three alignments), `bar-states` (0, 0.5, 0.86 with threshold, vertical),
`image-fits` (cover/contain/stretch, radius 0 and 12, opacity 0.5) with a checked-in 60x90 PNG,
`repeater-auto` (three items with auto cell height). Base is a flat `#FF203040`. Values come from a
fixed `ValueTree`, never from real sources; `time` is a literal.

`Compare.Diff(Surface a, Surface b, int tolerance = 8) : (int DifferentPixels, Rect? Bounds)`;
a test fails when more than 0.1 percent of pixels differ by more than `tolerance` in any channel,
and on failure writes `<name>.actual.png` and `<name>.diff.png` next to the golden for inspection.
`GOLDENS_UPDATE=1` in the environment rewrites the goldens instead of comparing (documented in the
test file header; never set in CI).

- [x] **Step 1:** write the four layouts and `Compare`; generate goldens with `GOLDENS_UPDATE=1`; commit them with the message stating the renderer commit they were made from.
  *Evidence:* 0d477df (four goldens), 136c217 (a fifth, `dial`).
- [x] **Step 2:** deliberately change a colour in a layout, see the test fail and the diff PNG appear, revert.
  *Evidence (re-proven 2026-09-30):* `"fill": "#FFFF0000"` on the half bar in
  `Goldens/layouts/bar-states.json` -> `GoldenTests.Bar_States` failed with "11700 of 309600
  pixels differ ... bounds X=40,Y=110,W=390,H=30" and wrote `bar-states.actual.png` and
  `bar-states.diff.png` beside the golden in the test output folder. Reverted; all 5 golden
  tests pass; tree clean.
- [x] **Step 3: Commit** (0d477df, same message) `git commit -m "Golden-image tests for text, bars, images and repeaters"`

---

### Task 4: Test hygiene from the Phase 1 review (lane `lane/p6-tests`, Sonnet)

Review minor 18: three tests depend on machine state (`WallpaperSetterTests.Get_ReturnsCurrentPath_ForPrimary`,
`DisplaySignatureTests.Enumerate_Returns_At_Least_Primary`, `DisksSourceTests`). Keep them (they are the
only real-desktop coverage) but give each a `[Trait("Category", "Desktop")]` and make them
`Skip` with a reason when `Monitors.Enumerate()` is empty or no fixed drive is ready, so a CI box
without a desktop session reports skipped, not failed. Also add the missing corrupt-`.lnk` test
for `ShortcutFiles.Read` (Phase 3 deferred minor).

- [x] Commit `git commit -m "Tests: desktop-bound tests skip without a desktop; corrupt .lnk read test"`
  *Evidence:* 7103543, same message. `[Trait("Category", "Desktop")]` on the WallpaperSetter,
  DisplaySignature and Disks tests (and later desktop-bound ones);
  `ShortcutFilesTests.Read_Corrupt_Lnk_Returns_Null`.

Lane `lane/p6-tests` complete.

---

### Task 5: Documentation (lane `lane/p6-docs`, Sonnet)

**Files:**
- Rewrite: `README.md` (human overview of v1: what it is, install, first run, layouts, budget numbers from Task 2, how verify works, uninstall, the POC's place in history)
- Create: `docs/layout-format.md` (every component type and property, the binding grammar with ten examples, repeater semantics including auto cells and clamping, display signatures and scaling), `docs/sources.md` (every source type, its settings, what it publishes, failure behaviour, the secrets file), `docs/architecture.md` (the process model in one page, the tick pipeline, where files live in the runtime dir, the budget and how it is enforced)
- Modify: `CLAUDE.md` (fold the "v1 gotchas" into the main structure; move POC-only sections under a "POC (retired)" heading; update the file table to the v1 tree)

Rules: no marketing adjectives; every number comes from a measurement file; every command shown
was run. The layout-format doc is generated by hand from `ComponentDef.cs`, `PropertySchema.cs`
and the JSON tests, and each property's default is the one in the code.

- [x] Commit `git commit -m "Docs: README for v1, layout format, sources, architecture; CLAUDE.md restructured"`
  *Evidence:* 276d16f, same message; kept current since (989b316 for layout format v2). The
  verify text was still the plan's, not the shipped command's; 13d4293 rewrote README "Verify",
  the architecture paragraph and runtime-file rows, and the two "in progress" notes in CLAUDE.md
  from the code and the 2026-09-30 runs. Budget numbers: c2be4c8.

---

### Task 6: Starter layouts and the `layouts/` folder (lane `lane/p6-docs`, Sonnet)

Reconcile `layouts/` with what Phase 5 shipped: `starter-column.json`, `starter-clock.json`,
`starter-blank.json`, `steam-recent.json`, `clock-disks.json`, each opening cleanly in the designer
and rendering with `deskwall tick --layout <file> --force --no-apply` on the ultrawide signature.
`layouts/README.md` lists them all with one sentence each and the secrets section.

- [x] Commit `git commit -m "layouts: starters reconciled with the designer; README"`
  *Evidence:* the `starter-*.json` names were superseded before this task ran: Phase 5 and the
  v2 format shipped `clock-disks`, `steam-recent` and `column-system` as generated starters
  (`StarterGeneratorTests` pins them), plus the hand-authored `alpine-vision`. 58c6c89 adds
  `RepoLayoutsTests` (every `layouts/*.json` opens as a `DesignerModel` on the 3440x1440 canvas
  and previews with no expansion problems: 4 passed) and lists `alpine-vision.json` in
  `layouts/README.md`. Rendered 2026-09-30 with the AOT exe, `tick --layout <file> --force
  --measure --no-apply --no-shortcuts`, scratch home, DELA243 3440x1440, all exit 0:
  alpine-vision 696 ms / 438 ms CPU (31 components), clock-disks 328 / 219 (7), column-system
  313 / 141 (22), steam-recent 122 / 125 (7; no Steam secrets in the scratch home).

Lane `lane/p6-docs` complete.

---

### Task 7: Parity gate and POC retirement (controller)

- [ ] Every box in the master plan's parity gate ticked with evidence pasted into the ledger:
  starter column reproduces the POC column and `deskwall verify` reports 5/5 on every cover;
  budget table passes under AOT; the spec section 9 manual acceptance list walked; the daemon has
  run 24 hours with zero tick failures in `deskwall.log`.
  **Status 2026-09-30:** not closable from JOES-XPS-17. Per box, see "Parity gate status" below.
- [ ] `Disable-ScheduledTask 'DeskWall Tick'` then `Unregister-ScheduledTask`; `deskwall install`.
  **TO RUN ON JOES-PC:** runbook R6, only after R1-R5 pass.
- [ ] `git rm -r poc/` and remove the POC sections from `CLAUDE.md` and `README.md` (one commit:
  "Retire the PowerShell POC: v1 reached parity on <date>").
  **Prepared, not merged:** the last commit on `spike/parity-gate` is exactly this, labelled
  "Retire the PowerShell POC (merge only after the JOES-PC parity runbook passes)". Fill in the
  date when merging (runbook R7).
- [ ] Opus whole-branch review of Phases 2 to 6 changes (one agent), ONE fix wave, re-review.
- [ ] Tag `v1.0.0-rc1` on `v1`. Merging `v1` into a `main` branch is Joe's call (finishing-a-development-branch).
  **Not done:** there is no `v1` branch any more (`main` is the integration branch), and the
  gate is not closed. Tag `main` after the merge, if Joe wants it.

## Parity gate status (2026-09-30, JOES-XPS-17)

| Master plan box | Status | Evidence / what is left |
|---|---|---|
| Starter reproduces the right-hand column; `verify` 5/5 on every cover | Open: needs JOES-PC | `verify` is finished and tested (Task 1). This machine's desktop folder view reports unavailable, so no shortcut can be measured here. R2. |
| Every line of the budget table passes | **Open: owner decision** | 3 of 5 rows pass on both machines. Handles/threads and the clock-only tick are over on both, and the 2026-09-30 attribution shows why: the resident D3D/D2D/DWrite/WIC stack and the OS thread pool, not a leak or a tunable. Either change spec 3.1's process model (render out of process) or restate the two rows as measured. Spike results, "Parity-gate pass". |
| Spec 9 manual acceptance walked | 2 of 6 walked here; 4 need JOES-PC | See the table below. R3. |
| 24 h with zero logged tick failures | Open: needs JOES-PC | R4. |

Spec 9 manual acceptance:

| Item | Status | Evidence |
|---|---|---|
| Apollo resolution change repairs itself | Wallpaper half seen here; icons need JOES-PC | Unplanned on 2026-09-30 at 20:25:04: the ultrawide went away mid-run and two scratch daemons logged `scaled layout ... to ...SHP1517... 1920x1200` then `tick DisplayChange (1920x1200): redrawn 7 total 206 ms`. Icon re-placement: R3a. |
| Explorer restart keeps icons | Fix made here; icons need JOES-PC | The review found nothing re-placed icons after an Explorer restart: `TaskbarCreated` only re-added the tray icon, and an unforced tick skips shortcuts whose fingerprint is unchanged. The host window now raises a forced, 2 s-delayed `ExplorerRestarted` wake (tray or not). Live on a scratch daemon (`TaskbarCreated` posted to its own window only): `tick ExplorerRestarted (taskbar): redrawn 7 total 57 ms`. Whether Explorer then keeps the positions: R3b. |
| Sleep and wake resumes on schedule | Needs JOES-PC | Sleeping this laptop would end the agent session. R3c. |
| Dead `http` endpoint leaves the old value | **PASS** (scratch home, AOT exe) | A local `http` source (`every` 15 s) bound to a 96 px text; `python -m http.server` served `{"v":"ALPHA"}`, then was killed. 40 s later the text's pixels in `deskwall.jpg` hashed identical and the daemon had not repainted (Timer ticks `skipped`). Control: serving `{"v":"BRAVO"}` changed the hash. As designed (`docs/sources.md`), the value is dropped once the source has missed 3 of its own refreshes: `[WARN] source 'probe' is stale (missed 3 refreshes)`, 43 s after the kill at `every` 15, so a 900 s weather source holds its last value ~45 minutes. |
| Malformed layout keeps the old wallpaper | **PASS** (same run) | Truncated the registered layout mid-array. Daemon logged `[ERROR] layout ...accept.json cannot be read: JsonException: '2' is an invalid end of a number...` and `[WARN] no layout for ...; waiting`, stayed alive, retried once a minute, and did not touch `deskwall.jpg` (mtime unchanged over 75 s, crop hash unchanged). |
| Uninstall leaves the desktop as found | Needs JOES-PC | R5. |

Observed along the way, not a gate item: while the endpoint was down, Timer ticks that had
nothing to redraw still took ~2 s of wall (0 ms CPU). The `http` refresh awaits its fetch up to
its `timeout` on the tick, and Windows retries a refused connect to a closed local port for
about 2 s. CPU budget unaffected; noted in case the owner wants refreshes fully off the tick.

## JOES-PC runbook (to close the gate)

Run in a normal PowerShell 7 terminal on JOES-PC at the console (not RDP: the signature changes
and screenshots of the console are impossible). `deskwall.exe` is a WinExe, so use this helper
to wait for it and see its output:

```powershell
function dw { $o = "$env:TEMP\dw-o.txt"; $e = "$env:TEMP\dw-e.txt"
  $p = Start-Process "$env:LOCALAPPDATA\Programs\DeskWall\deskwall.exe" -ArgumentList $args -Wait -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
  Get-Content $o, $e; Write-Host "exit $($p.ExitCode)" }
```

**R0. Get the branch without disturbing the POC.** The `DeskWall Tick` task runs `poc\` from
the owner's main checkout, and the branch's last commit deletes `poc\`, so never switch that
checkout to the branch before R6. On JOES-XPS-17, push it once:
`git push -u origin spike/parity-gate`. Then on JOES-PC, in a separate worktree at the commit
before the POC deletion:

```powershell
(Get-ScheduledTask 'DeskWall Tick').Actions      # note which checkout the POC runs from (<POC checkout> in R2)
git -C <main checkout> fetch origin
git -C <main checkout> worktree add ..\DeskWall-parity origin/spike/parity-gate~1
cd ..\DeskWall-parity
git log -1 --format=%s                           # must NOT be "Retire the PowerShell POC ..."
dotnet build            # expect 0 warnings
dotnet test             # expect all green (2026-09-30 on the XPS: 522 Core, 595 Designer)
```

**R1. Budget suite (reference numbers).** Close the ultrawide's other apps; the suite repaints
the wallpaper with a scratch layout for ~5 minutes.

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"   # harness only; harmless in a terminal
dotnet publish src/DeskWall.Daemon -c Release -r win-x64
Disable-ScheduledTask -TaskName "DeskWall Tick"
try { dotnet test tests/DeskWall.Core.Tests --filter Category=Budget --logger "console;verbosity=detailed" }
finally { Enable-ScheduledTask -TaskName "DeskWall Tick" }
```

Paste the five `| ... |` rows under "Phase 6 budget results". Expect the same two OVER rows;
then make the budget decision above.

**R2. Starter column vs the POC column, and verify 5/5.** Needs the Steam secrets from
`layouts/README.md` in `%LOCALAPPDATA%\DeskWall\secrets.json`, and this branch installed.

```powershell
powershell -NoProfile -File <POC checkout>\poc\verify.ps1   # the POC's column, for comparison
Copy-Item "$env:LOCALAPPDATA\DeskWall\verify-desktop.png" "$env:LOCALAPPDATA\DeskWall\verify-desktop-poc.png"
Disable-ScheduledTask -TaskName "DeskWall Tick"     # the POC stops painting and placing slots 0..3
scripts\publish.ps1 -Aot                            # from the worktree: installs this branch and restarts the daemon
dw layouts set layouts\steam-recent.json
dw calibrate
Start-Sleep 70                                      # one tick places the four cover shortcuts
dw shortcuts                                        # planned vs reported, every row should match
dw verify                                           # expect 4 x "left pad 5, bottom pad 5 ... OK", "RESULT: OK", exit 0
dw verify --json; Copy-Item "$env:TEMP\dw-o.txt" "$env:LOCALAPPDATA\DeskWall\verify-report.json"
```

Compare `verify-desktop.png` with `verify-desktop-poc.png` side by side (same clock position,
same four covers in the same order, same drive rows) and look at `clock-now.png`. Leave the POC
task disabled from here; if anything fails, `Enable-ScheduledTask -TaskName "DeskWall Tick"`
puts the POC back while it is fixed.

**R3. Spec 9 manual acceptance.** After each, `dw verify` must still say `RESULT: OK`.

- a. Apollo: start a stream at a different resolution, end it. `deskwall.log` shows
  `tick DisplayChange (...)` for each change and `shortcuts: placed 4` after the last.
- b. Explorer: `Stop-Process -Id (Get-Process explorer).Id -Force; Start-Process explorer`.
  About 2 s after the taskbar comes back the log shows `tick ExplorerRestarted (taskbar)`
  followed by `shortcuts: placed 4`; then `dw verify` is `RESULT: OK`. If Explorer moves the
  icons again after that tick (it lays the desktop out from its own saved positions), that is a
  finding: record it rather than re-running until it passes.
- c. Sleep: Start > Power > Sleep for at least 3 minutes, wake. Log shows
  `tick SessionUnlock (resume)` within seconds of waking, and the clock is right within a minute.
- d/e. Dead endpoint and malformed layout: passed on the XPS (above). To repeat here, point a
  scratch copy of the layout's weather source at `http://127.0.0.1:9/` and watch the value hold
  until `is stale`; truncate a scratch layout and watch `cannot be read` with the wallpaper kept.

**R4. 24 hours.** R3 deliberately provokes errors (a malformed layout logs `[ERROR]`), and the
log keeps up to 2 MB of history, so count only lines written after the window starts. Leave the
daemon running a full day with the POC task disabled:

```powershell
$start = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')        # when the 24 h begin
# ... 24 hours later:
Get-Content "$env:LOCALAPPDATA\DeskWall\deskwall.1.log", "$env:LOCALAPPDATA\DeskWall\deskwall.log" -ErrorAction SilentlyContinue |
  Where-Object { $_.Length -ge 19 -and $_.Substring(0, 19) -ge $start -and $_ -match '\[ERROR\]|tick .* failed' }   # expect nothing
```

**R5. Uninstall leaves the desktop as found.**

```powershell
$before = Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Force | Select-Object -ExpandProperty Name
dw uninstall
$after = Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Force | Select-Object -ExpandProperty Name
Compare-Object $before $after       # expect only the four non-breaking-space slot shortcuts v1 owns
(Get-ItemProperty 'HKCU:\Control Panel\Desktop').WallPaper   # the pre-DeskWall wallpaper (restore.json)
dw install                          # back on
```

**R6. Retire the POC task** (plan Task 7 item 2):

```powershell
Disable-ScheduledTask -TaskName "DeskWall Tick"
Unregister-ScheduledTask -TaskName "DeskWall Tick" -Confirm:$false
dw install
```

**R7. Merge.** Tick the four master-plan boxes and this plan's Task 7 items with the evidence
above, put the date into the POC commit's subject ("v1 reached parity on <date>"), merge
`spike/parity-gate` into `main`, and only then update the main checkout (its `poc\` goes away,
which is fine now the task is unregistered). `git worktree remove ..\DeskWall-parity`.

## Self-review notes

- Spec 9 bullets: unit (all phases), golden (Task 3), budget (Task 2), verify (Task 1), manual acceptance (Task 7).
- Spec 10 parity gate and POC removal: Task 7.
- Master plan's Global Constraints: goldens tracked; budget tests need AOT; `.gitignore` already scoped in Phase 1.
- Type names: `Screenshot.Capture`, `Calibrator.DiffBounds`, `ShortcutPlan.IconPosition`, `Calibration.Load`, `DesktopView.GetPosition` as shipped by Phase 3.
