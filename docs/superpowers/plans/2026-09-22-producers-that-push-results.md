# Producers that push: what was measured

Numbers for `docs/superpowers/plans/2026-09-22-producers-that-push.md`. Each lane records its own
half; nothing here is asserted.

## Lane A: the file watcher, the streaming command, and the one wake path

Machine: JOES-PC, i7-6700K, Windows 11, 3440x1440 primary. Binary: the published native-AOT
`deskwall.exe` (`dotnet publish src/DeskWall.Daemon -c Release -r win-x64`), not the JIT test
host, run as `deskwall --home <scratch> run --no-tray --no-shortcuts` against a scratch runtime
directory inside the worktree. The owner's live daemon was left running throughout; a scratch
`run` paints the real wallpaper, and the live daemon reclaimed it on its next content change.

Scripts: `.measure/setup.ps1`, `.measure/latency.ps1`, `.measure/at-rest.ps1` (not committed; the
layouts they generate are three-source variants of the same clock-plus-file-plus-producer shape).

### A file save to the wallpaper changing

Measured end to end from `Set-Content` on the watched file returning, to `deskwall.jpg` in the
scratch home having a new last-write time. Eight saves, three seconds apart, one `file` source and
a clock in the layout, nothing else changing.

| | ms |
|---|---|
| samples | 759, 761, 767, 793, 780, 766, 765, 766 |
| min / median / max | 759 / 766 / 793 |

Cold start to the first frame in the same run: **184 ms**.

The 766 ms is accounted for and is almost all deliberate waiting:

| Stage | ms |
|---|---|
| `FileSource` debounce (the watcher raising `Changed`) | 300 |
| `EventBus` coalescing window (`Signal` to `WakeRequested`) | 400 |
| wake, tick, resolve, redraw one component, encode and write ~2 MB of JPEG, set the wallpaper | ~66 |

The spread is 34 ms across eight samples, so the two fixed waits dominate and the work does not
vary. Before this change the same save waited out the mtime re-check, which was `every` = 30 s and
is now 300 s, so the comparison is 766 ms against up to 30 s (and, on the new default, up to
300 s if the watcher had not been wired).

Shortening it is possible and was not done: the two constants exist for reasons that still hold.
The 300 ms is LayoutWatcher's, and one save raises two file-system events on this machine (three
or four for a save-by-rename), each of which would otherwise be a two-megabyte repaint. The 400 ms
is the bus's, and it is what makes a burst of producers cost one repaint.

### One wake path, and what a wake is worth

The phase collapses four out-of-band wakes into one: an async fetch landing after its tick gave
up, an image arriving, a watched file changing, and (lane B) a volume callback. All of them now
raise `ISignalSource.Changed`, `SourceSignals` turns that into `EventBus.Signal(name)`, and the
daemon subscribes to `WakeRequested` once.

A thing worth recording because it cost the audio lane a measurement and nearly cost this one:
**a signal buys a wake and nothing else.** The tick that follows still asks
`Scheduler.IsDue` which sources to refresh, and a source that signals while saying it is not due
wakes the machine and changes nothing on screen. Lane B measured that as eight volume changes and
a six-second drag producing zero repaints. Every signalling source therefore carries a pending
flag, set in the same branch that raises `Changed` and cleared in `RefreshAsync`, and `NextDue`
answers "now" while it is set. Clearing it matters as much as setting it: a source that is
unconditionally due pins the daemon's wake at `Scheduler.MinDelay`, four ticks a second.

### A streaming command at rest, against the same command polled

Two four-minute windows, one daemon each, run one after the other so they did not contend. Both
layouts are identical apart from the producer: a clock, a `file` source nothing was writing to,
and one `command` source. The producer is one `.cmd` script run two ways -- held open and printing
a JSON line once a minute (`stream: true`), or printing the same line once and exiting
(`every: 300`). Sampling began twenty seconds after the first frame, so the cold start, the image
cache sweep and the first working-set trim are outside it.

| Over 240 s at rest | `stream: true` | polled `every: 300` |
|---|---|---|
| handles, start -> end | 307 -> 311 | 309 -> 304 |
| threads, start -> end | 19 -> 11 | 18 -> 9 |
| daemon CPU | 453 ms | 203 ms |
| of which the daemon logged as tick CPU | 437 ms | 203 ms |
| **CPU outside ticks** | **16 ms** | **0 ms** |
| working set between wakes | 0.3 MB | 0.7 MB |
| child processes | one cmd.exe, 8.5 MB | none |
| ticks in the window | 8 | 4 |

Read across the two end states rather than the deltas -- the thread counts fall in both because
the pool sheds workers after start, and that is not what is being measured. The steady-state cost
of holding a producer open is **+7 handles and +2 threads**: a process handle, its two redirected
pipes, and the one dedicated stdout reader.

The CPU is the number that matters and it is **16 ms in four minutes**, which is one scheduler
quantum: `TotalProcessorTime` moves in steps of 15.6 ms on this machine, so a blocked reader
thread is at or below what the counter can see. It costs nothing at rest, as a thread blocked on a
read should.

What it does cost is the producer's own process: 8.5 MB of cmd.exe that never goes away, against a
polled command that is not resident at all. That is the trade, and it is the producer's
footprint rather than DeskWall's -- a producer written as a small native binary would cost less, a
PowerShell one a great deal more.

**The poll's cost is not zero either, it is just somewhere else.** A four-minute window at
`every: 300` caught no spawn at all, so the table above flatters it. One `deskwall tick --measure
--force` against each home, three runs, isolates it:

| resolve stage | run 1 | run 2 | run 3 |
|---|---|---|---|
| polled | 34 ms | 35 ms | 35 ms |
| streaming | 8 ms | 8 ms | 8 ms |

About **27 ms per poll**, and it is on the tick's critical path: the source starts cmd.exe and
waits for it to finish before the frame can be resolved. The streaming source's 8 ms is a process
start too (a one-shot `tick` starts a fresh one), but it returns without waiting for a line, so
it is never on the path. In the resident daemon it is paid once, at start.

### An idle machine still wakes once a minute

Both windows, from the daemon's own log:

- polled: **4 ticks in 240 s, all `Timer`**, at 08:04:00.080, 08:05:00.086, 08:06:00.084,
  08:07:00.070. One a minute, on the minute, within 90 ms.
- streaming: **8 ticks in 240 s -- 4 `Timer` and 4 `SourceCompleted`**. The four `Timer` ticks are
  the same once-a-minute clock. The four others are the producer printing its line, which is the
  feature working, not overhead; a producer that says nothing costs nothing. The `file` source
  contributed no wakes at all over either window, because nothing wrote to the file.

So the watcher and the resident producer add no wakes of their own. Every tick in eight minutes is
either the clock or a producer with something to say.

### What was not measured, and what to watch

- The polled baseline's four-minute window contained no spawn, which is why the per-poll cost is
  measured separately above rather than being visible in the table.
- The producer used here prints once a minute. A chatty one -- a volume slider, lane B's case --
  is bounded by the bus's 400 ms coalescing window rather than by anything in this source, and
  lane B measured that.
- `deskwall verify` was not run: nothing in this lane moves a shortcut or changes any layout's
  geometry.
- A scratch `deskwall run` paints the real wallpaper, so the owner's desktop was taken over for
  the length of each run and reclaimed by the live daemon on its next content change.

