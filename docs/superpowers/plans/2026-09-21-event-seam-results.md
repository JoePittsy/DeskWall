# Event seam (phase 1): what was measured

Date: 2026-09-21. Machine: JOES-PC, i7-6700K, Dell U3425WE at 3440x1440 @ 100%, console session.
Spec section 10 lists what had to be measured rather than asserted; this is each of those, with
how. Everything here is the published native-AOT daemon (`dotnet publish src/DeskWall.Daemon -c
Release -r win-x64`), never a JIT test host, and always against a scratch `--home`: the owner's
installed daemon was left running throughout.

**Baseline for the comparisons** is commit `67f6355` (lane 1's Core work merged, no pipe, no bus
in the daemon), published from a throwaway worktree with one line changed: the single-instance
mutex name, so it could run beside the owner's daemon. Nothing else differs.

**The layout** both binaries ran is a clock plus two text components bound to a pushed provider
(`build.data.status`, `build.ageSeconds`), so the seam is exercised and the render cost is small
enough not to drown it.

## 1. Handles and threads, before and after

Sampled with `Get-Process`, every 20 s, after the process had been up long enough for its first
render to be collected (`Footprint.Release` runs at the end of every tick).

| | handles | threads | private bytes |
|---|---|---|---|
| baseline (no seam), idle | 254 - 256 | 8 - 9 | - |
| this branch, seam present, nothing pushed | 287 - 293 | 9 - 10 | 8.5 MB |
| this branch, two providers pushed and bound | 293 - 295 | 9 - 10 | 8.5 MB |

Corroborated by the project's own budget test (`BudgetTests.Idle_Handles_And_Threads`, which
samples at exactly 3 minutes idle and runs its own scratch daemon), run once against each binary:

| | result |
|---|---|
| baseline `67f6355` | **277 handles, 8 threads** (FAIL: budget is < 100 h / < 5 t) |
| this branch | **316 handles, 10 threads** (FAIL) |

**The seam costs about +39 handles and two threads by the budget test's own reckoning** (316/10
against 277/8), and about +35 handles and one thread by the 20-second sampling above. Two
threads at the 3-minute mark is the accept thread plus one the pool had not trimmed yet; the
sampling sat at 9 to 10 against the baseline's 8 to 9 for as long as it was watched. One thread
is exactly what spec section 6
said it would be: `EventPipeServer` keeps one background thread blocked in `ConnectNamedPipe`,
which costs no CPU at rest, and adds a second only while a producer is actually connected. It is
its own thread rather than a pool work item deliberately: the pool is what the tick and every
async source use, and an event should not queue behind a render.

**The handle delta is more than "a small number" and is not fully explained.** One listening pipe
instance plus one thread is a handful of handles; the rest is unattributed and was not isolated.
Worth a look before this is called finished.

**The absolute numbers already fail the documented budget, and did before this lane.** 277 at the
base commit against a budget of 100, and 8 threads against 5. That is pre-existing and this lane
did not cause it, but `Idle_Handles_And_Threads` is red either way and somebody should decide
whether the budget or the daemon is wrong.

## 2. An idle machine still wakes once a minute

Ten consecutive minutes from the scratch daemon's own log, with a provider in the registry and
nothing being pushed:

```
22:31:00.083 tick Timer: redrawn 1 total  79 ms cpu 31 ms
22:32:00.115 tick Timer: redrawn 1 total 110 ms cpu 47 ms
22:33:00.084 ...
...
22:40:00.088 tick Timer: redrawn 1 total  82 ms cpu 62 ms
```

One wake per minute, at :00.08 to :00.18, one component redrawn each time (the clock). No extra
wake from the seam, and nothing between the minutes. Method: start the daemon, leave it alone,
read `deskwall.log`.

## 3. A burst of events

Method: one connection, N lines written, then the log's tick lines counted and the final value
read back off the rendered `deskwall.jpg`.

| burst | repaints | final value on the wallpaper |
|---|---|---|
| 50 lines in 24.8 ms | **1** | `build step 50`, correct |
| 50 lines over 2345 ms (a slider drag) | **6** | `build drag 50`, correct |

Six for the drag is one per 400 ms coalescing window plus the trailing one, which is the designed
behaviour, and the tick after the last event of the drag is the one that painted `drag 50`. The
final state always lands because the window is trailing, not leading. Both rows were measured
again after the fix in section 8 and came out the same.

## 4. The cost of one accepted event, end to end

Method: `LastWriteTimeUtc` of the scratch home's `deskwall.jpg` captured, the documented
PowerShell one-liner run, then the file polled every 5 ms until it changed. Timed from just
before the client connects.

| stage | ms |
|---|---|
| client: connect + write + dispose | 11.9 |
| coalescing window (deliberate) | ~400 |
| tick: resolve, incremental draw of 2 components, encode 2 MB JPEG, apply | 58 |
| **write to new wallpaper on disk** | **482** |

The coalescing window is 80% of it and is the point of the design; the daemon's own share is
about 60 ms. Log for the same event:

```
23:21:32.393 [INFO] events: provider 'build' seen
23:21:32.855 [INFO] tick SourceCompleted (posted): redrawn 2 total 58 ms cpu 62 ms
```

## 5. The documented one-liners

Both were run verbatim against the scratch daemon and both worked **unmodified**.

- The PowerShell one-liner from the plan's Task 9 step 2: connected, the value landed, and the
  crop of the rendered wallpaper reads `build green` / `0 s ago`. No change to the plan's text
  was needed.
- `cmd /c 'echo {"source":"shell","data":{"note":"from cmd"}} > \\.\pipe\DeskWall.Events'`:
  accepted and repainted. Added to `docs/sources.md` as the shell form.

## 6. Diagnostics, checked by sending bad lines

```
22:42:45.230 [INFO] events: rejected (invalid json: 'not json at all' is an invalid JSON literal...): not json at all
22:42:45.230 [INFO] events: rejected (missing "source"): {"data":{"x":1}}
22:42:45.230 [INFO] events: rejected ("source" "my build" is not a valid binding name: ...): {"source":"my build","data":{}}
22:42:45.231 [INFO] events: provider 'time' seen
22:42:45.633 [WARN] event provider 'time' is shadowed by a layout source of the same name; the layout source wins
```

The connection survived all four lines. The clash warning fires once per name per run, and again
after a restart because the record is restored.

## 7. Persistence across a restart

Killed the daemon and started it again on the same scratch home: both providers (`build`, from
the PowerShell client, and `shell`, from the cmd one) came back from `events.json` with their
merged payloads and their original `receivedAt`, and the first tick painted `build drag 50`
without any producer running.

## 8. The bug the measuring found

The first implementation used an asynchronous pipe and lost lines. A pipe instance is
connectable the moment `CreateNamedPipe` returns, but its `ConnectNamedPipe` is only pending
once the server asks for a connection; a producer that connects, writes and disconnects inside
that window makes the connect fail with ERROR_NO_DATA and the line was discarded.
Connect-write-disconnect is the documented way to send one event, so this was the main path, not
a corner.

It first showed up as an intermittent full-suite test failure (about one run in three, never in
isolation), which is why the class now has
`Twenty_Connect_Write_Disconnect_Producers_In_A_Row_All_Land`: before the fix it lost one or two
of the twenty on seven runs out of eight; after it, eight runs out of eight are clean, and so are
five full-suite runs.

The fix reads the line anyway. The bytes are still in the instance's buffer, and the handle can
be wrapped in a second `NamedPipeServerStream` that is told it is connected. That wrap is only
legal on a non-overlapped handle, so the server is synchronous now, which is also what put the
thread count in section 1 at one instead of zero. Pre-arming all four instances was tried first
and does not work: Windows will hand a client to a listening-but-not-yet-connected instance even
when armed ones are available.

## Known gaps, measured or found while measuring

- **About +35 to +39 handles unattributed** (section 1), on top of a handle budget that was
  already red before this lane.
- **Two daemons share one pipe name.** Windows lets a second process create another instance of
  an existing named pipe when the ACL allows it, so two daemons on two scratch homes both listen
  on `DeskWall.Events` and a producer's connection goes to whichever instance is next. Only
  reachable by deliberately running a second `--home`, but it means "which daemon got my event"
  is undefined in that case.
- **Forget is not durable while the daemon is running.** The daemon owns `events.json` and never
  re-reads it, so a record the designer removes comes back at the daemon's next save. The panel
  says so; closing it properly needs the daemon to watch the file, which is not in this phase.
- **`expectEverySeconds` is carried and shown but does not yet make a provider go stale.** Phase
  2, as the plan's self-review already noted.
