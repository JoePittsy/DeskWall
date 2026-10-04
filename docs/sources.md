# Sources

A source is declared in a layout file's `sources` array:

```json
{ "name": "steam", "type": "http", "every": 600, "settings": { "url": "...", "unixTimeFields": "..." } }
```

| Field | Notes |
|---|---|
| `name` | The root field bindings use to reach this source's values (`steam.json...`). |
| `type` | One of `time`, `disks`, `system`, `hardware`, `audio`, `battery`, `media`, `notifications`, `command`, `http`, `rss`, `file` (`SourceFactory.Create`). Anything else throws when the layout is loaded. |
| `every` | Seconds between refreshes. Optional; each type has its own default (below). Ignored by `time`, which is always due on the next whole minute. **A top-level field, not a `settings` key** -- each type's "Settings" line below names it only to give its default, and a source that finds `every` inside `settings` ignores it. |
| `settings` | A flat string-to-string map; each source type documents its own keys below. |

Source of truth for this document: `src/DeskWall.Core/Sources/*.cs` (the `FromDef` method on
each source reads its own `settings` keys and defaults) and `SourceFactory.cs`.

## Failure and staleness (applies to every source)

- A source that throws from `RefreshAsync` is marked failed (`SourceSnapshot.Failed`); its
  **previous** successful values keep publishing (spec 3.2 -- a partial or blank record must
  never overwrite the last good one).
- A failing source is retried on a backed-off schedule: its own interval, doubled per
  consecutive failure, capped at 15 minutes (`Scheduler.BackOff`). This is also what the daemon's
  wake timer uses, so a dead endpoint does not pin the daemon awake on `MinDelay` (250 ms)
  forever.
- The daemon (not `deskwall tick`, which runs once) additionally drops a source's values from the
  tree entirely once it has missed `StaleAfter` (3) of its own scheduled refreshes
  (`SourceRegistry.Tree(sources, now)`), so a bound component falls back to its own default
  instead of showing a number that is merely old. `StaleChanged` logs one WARN going stale and
  one INFO coming back, not one line per tick.
- Network and command sources never run on the tick thread (`AsyncSource`); a tick reads whatever
  the last completed fetch published, and a fetch finishing later posts a wake so the next tick
  picks it up.

## `time`

Settings (both optional): `sunrise`, `sunset`. Always due on the next whole minute (`NextDue`
rounds up to `:00`).

Publishes: `now` (`TimeValue`), `date` (`TextValue`, `yyyy-MM-dd`), `weekday` (`TextValue`, e.g.
`Saturday`), and how far through the day, week and year local time is:

| Field | Meaning |
|---|---|
| `dayFraction`, `weekFraction`, `yearFraction` | 0..1, rounded to 4 decimals |
| `dayPercent`, `weekPercent`, `yearPercent` | the same, 0..100, rounded to a whole number |
| `phase` | `night`, `dawn`, `day` or `dusk`. Without sun settings, from `dayFraction`: dawn from 0.21, day from 0.29, dusk from 0.71, night from 0.83. With them, from the real sun: dawn is sunrise ±40 min, dusk is sunset ±40 min, day in between, night otherwise |
| `sunFraction` | 0 at sunrise, 1 at sunset, clamped (0 before sunrise, 1 after sunset), 4 decimals: a sun bound to it rises and sets when the real one does. 06:00 to 18:00 without settings |
| `nightFraction` | The same across the night: 0 at sunset, 1 at the next sunrise, 0 all day |
| `sunrise`, `sunset` | `TimeValue`s today, only when the settings are given (`time.sunset \| HH:mm`) |

`phase` exists so a layer that only needs four colours is one Step rule
(`time.phase | "?night=#..,dawn=#..,day=#..,dusk=#.."`), and the palette for that layer lives in
that one place. Use a Blend on `dayFraction` where the change should be gradual (or on
`sunFraction`/`nightFraction`, which follow the real sun).

Both forms exist because a `bar`'s `fraction` wants 0..1 and a `text` wants the percent, and a
format string cannot multiply by 100. The **week starts on Monday** (`((int)DayOfWeek + 6) % 7`),
not on Sunday. The year divides by 366 in a leap year and 365 otherwise. All three are derived
from the same local `now` the clock publishes, so a "day progress" widget needs no script.

### Real sunrise and sunset

`sunrise` and `sunset` each take either a fixed local `HH:mm` or a binding path to another
source's value. The path is resolved against the value tree at every refresh: the tree the
previous tick resolved against (`SourceTree.Latest`), so it lags by at most a minute. The value
it reaches may be:

- an ISO date-time (only the time of day is used; one without an offset is taken as local, one
  with an offset is converted);
- `HH:mm` text;
- a `TimeValue`, or a number read as Unix seconds.

A one-element record from a JSON array of scalars (`[0]` of `["..."]`) is unwrapped.

Until a path first resolves, 06:00 and 18:00 stand in. After that, the last pair that resolved is
kept while the other source is failing or stale, so the sky does not jump back to 06:00. A pair
with the sunset before the sunrise is ignored the same way. A setting that is neither `HH:mm` nor
a parseable path fails when the layout is loaded.

Open-Meteo gives both for free. Add `daily=sunrise,sunset` to the weather source's URL (the
`weather` widget's `widgets/weather.json`, which `column-system.json` places). Keep
`timezone=auto`, which it already has, so the times are the town's own local times:

```
...&current=temperature_2m,weather_code,is_day&daily=sunrise,sunset&timezone=auto
```

and point the time source at the first day:

```json
{ "name": "time", "type": "time",
  "settings": { "sunrise": "weather.json.daily.sunrise[0]", "sunset": "weather.json.daily.sunset[0]" } }
```

`deskwall tick --preview time.at=HH:mm` pins the time fields with the sun the live tick resolved
(`time.sunrise`/`time.sunset`, present when the settings are given), so `phase`, `sunFraction` and
`nightFraction` are what the desktop would show at that time today. `time.sunrise=HH:mm` and
`time.sunset=HH:mm` pins move the sun as well (and give one to a layout without the settings);
`scripts/gallery.ps1` pins 06:00 and 18:00 so its dawn and dusk scenes do not depend on the season.
Without either, `phase` keeps the fixed thresholds and `sunFraction` runs 06:00 to 18:00.

## `disks`

No settings read. Default `every`: 300 s.

Enumerates fixed, ready drives (`DriveInfo.GetDrives().Where(Fixed && IsReady)`). Publishes
`drives`: a list keyed by `letter`, each item: `letter`, `label`, `free` (bytes, `NumberValue`),
`total` (bytes), `freeGB`, `totalGB` (both rounded), `usedFraction` (0 when `total <= 0`).

## `system`

No settings read beyond `every` (default 900 s).

Publishes: `uptime` (seconds), `uptimeText` (`"3d 4h"` or `"4h 12m"` under a day), `bootedAt`
(`TimeValue`), `machine`, `user`, `pendingReboot` (`BoolValue`: true if any of the standard
Windows reboot-pending registry markers exists -- CBS `RebootPending`, Windows Update
`RebootRequired`, or a non-empty `PendingFileRenameOperations`), `daysSinceCrash` (`-1` if none
found), `lastCrashAt` (`TimeValue`, omitted entirely when no crash is known).

The crash scan reads at most the last 2000 entries of the System event log for id 41
(Kernel-Power) or 1001 (BugCheck) with entry type Error or Warning. It runs **once per process**
and is cached (`SystemSource.CachedCrashEvent`): the answer cannot change while the machine has
not rebooted, and the scan can take seconds on a busy log, which would otherwise block the tick
thread (which, in the daemon, is also the message pump) every 15 minutes.

## `hardware`

Settings: `every` (seconds between publishes, default 60), `sample` (seconds between readings,
default 10), `window` (seconds averaged over, default 300). Each is read from `settings` as a
positive number; anything unparseable or <= 0 falls back to the default. `every` is the ordinary
top-level `every` field.

CPU, RAM and (when an NVIDIA GPU is present) GPU load, averaged over a rolling window. A
timer inside the source (from `TimeProvider.System`; tests inject a fake), started on the first `RefreshAsync` and stopped on
`Dispose`, takes its first reading **immediately** and one more every `sample` seconds after that,
into a fixed ring of `window / sample` slots (at least 1). `RefreshAsync` itself only reads the
rings, so the daemon's schedule is unchanged: the source is due every `every` seconds like any
other, and the daemon still wakes once a minute. A reading that fails is skipped, not recorded as
zero.

**Cold start.** The very first `RefreshAsync` can only start the sampler, so it publishes
`samples: 0` and every bound property falls back to its own default -- which looked broken for up
to a minute when a hardware source was added in the designer. `NextDue` therefore says "due now"
after that first empty refresh, so the next wake (`Scheduler.MinDelay`, 250 ms later) paints real
numbers. Exactly **one** such wake is granted, and it is spent whether or not the reader produced
anything: a reader that never reads is a *successful* refresh publishing `samples: 0`, so the
scheduler's failure back-off does not apply to it and an unconditional "due now" would pin the
daemon's wake at 250 ms for ever. Once any ring has data the source is back on the whole minute
and shares the clock's single wake.

| Field | Meaning |
|---|---|
| `cpu` | average load over the window, 0..1 |
| `cpuPct` | the same as an integer 0..100, for text |
| `cpuNow` | latest single reading, 0..1 |
| `ram` | average used fraction over the window, 0..1 |
| `ramPct` | the same as an integer 0..100 |
| `ramUsedGB`, `ramTotalGB` | latest reading, decimal GB to one decimal |
| `gpu`, `gpuPct`, `gpuNow` | GPU utilisation, same shapes as `cpu`; absent with no GPU reader |
| `gpuMemory` | GPU memory used fraction, average over the window; absent likewise |
| `gpuTempC` | average GPU temperature, integer degrees C; absent likewise |
| `gpuTempFraction` | `gpuTempC / 100`, so a dial can bind it without arithmetic |
| `samples` | readings in the window right now (0..30 with the defaults) |
| `window` | window length in seconds |

All values are `NumberValue`. Fractions are published rounded to 3 decimals so a content key does
not change for a difference below a pixel (the same rule as `ResolvedBar`). A refresh with zero
samples publishes only `samples: 0` and `window`, and every bound property falls back to its own
default; nothing throws out of `RefreshAsync`.

CPU load comes from `GetSystemTimes` (`1 - dIdle / d(Kernel + User)`), so it needs two readings:
the first sample after a start contributes nothing. RAM comes from `GlobalMemoryStatusEx`.

**GPU.** NVML (`nvml.dll`), the library the NVIDIA driver installs in `System32`; the legacy
`%ProgramFiles%\NVIDIA Corporation\NVSMI\` folder is probed as a fallback. It is loaded with
`NativeLibrary.TryLoad` and called through unmanaged function pointers (no marshalling, native
AOT safe), so a machine with no NVIDIA GPU, no driver, or a failing `nvmlInit_v2` simply gets no
GPU reader and the source publishes none of the `gpu*` fields at all -- rather than fields that
are permanently missing a value. A layout that binds them must therefore have a sensible default
on each bound property.

**No CPU temperature.** There is no driverless, admin-free way to read core temperature on
JOES-PC (the ACPI thermal zone is access denied and is not a core reading anyway), and running
HWiNFO64 resident or shipping an MSR driver were both declined; GPU temperature from NVML is the
only temperature published.

## `audio`

No settings. `every` is accepted and ignored: there is nothing to poll.

The default playback endpoint's master volume, and the first source that is **pure push**.
CoreAudio's `IAudioEndpointVolumeCallback` fires the moment the volume or the mute flag moves and
the notification carries both new values, so the source never asks; it tells the host it has
something, the host coalesces (`EventBus.Signal`), and the next tick reads what the callback
already stored.

| Field | Meaning |
|---|---|
| `volume` | master scalar of the default playback endpoint, 0..1, rounded to 3 decimals |
| `volumePct` | the same as an integer 0..100, for text |
| `muted` | `BoolValue`; drives a colour through the map format (`docs/layout-format.md`) |
| `device` | the endpoint's friendly name, e.g. "Digital Output (3- High Definition Audio Device)" |

**A default-device change is pushed too.** A headset being plugged in is silent on the volume
callback by construction, because that callback is registered on the endpoint that is no longer
the default. An `IMMNotificationClient` (`CoreAudioDeviceNotifier`, behind
`IAudioDeviceNotifier`) covers it: `OnDefaultDeviceChanged` for the render endpoint in the
`eMultimedia` role, and `OnDeviceStateChanged` for any endpoint. The source counts it as a change
when the new default is not the endpoint it reads, when the endpoint it reads changes state
(unplugged, disabled), or when it reads none and any endpoint changes state (the first device
arriving). A microphone plugged into a machine with speakers is not a change, nor is a repeat of
the per-role notification after the refresh has already moved. A change is handled exactly like a
volume change - signal, due now, cleared by the refresh - and the refresh compares the default
endpoint id against the one the volume callback is on and re-registers when they differ.
`OnDeviceAdded`/`OnDeviceRemoved` (installed, not usable) and `OnPropertyValueChanged` (noisy, and
nothing published depends on it) are ignored. Re-registering is never done from inside a
callback, which CoreAudio forbids; and the refresh makes its COM calls outside the source's lock,
because unregistering the old volume callback can wait for a notification in flight whose handler
takes that lock.

**The schedule is the whole minute**, the same boundary `time` and `hardware` use, so the source
shares the clock's existing wake and asks for none of its own. It takes the very first reading
(which is also when both callbacks are registered, not when the layout loads) and is the
backstop for a device notification that never came. If the notifier cannot register, the source
still works on that minute schedule and retries the registration on every refresh; the outage is
one count in `ReaderFaults` and one warning in `deskwall.log` (`source 'audio': device-change
notifications unavailable (...)`), through `IWarningSource`, which the daemon drains after each
tick.

**No playback device publishes an empty record**, not zeros, so every bound property falls back to
its own default rather than drawing a confident "0%". A `bar` or `dial` whose bound `fraction`
does not resolve is drawn fully transparent (`docs/layout-format.md`), so the `volume` widget's
ring and the ridge lines in `alpine-*` and `vapor` disappear instead of reading as volume 0; the
widget's two text parts already resolve to empty. The same is true of a COM failure on the
endpoint: it costs that refresh and nothing more, and is counted rather than thrown, so the
source never lands in the scheduler's failure back-off for something the next minute fixes.

**Not every notification is a repaint.** CoreAudio fires per slider step and several steps land
inside one rounded percent, while a repaint re-encodes and writes about two megabytes. A
notification raises `Changed` only when the published form - endpoint id, volume to 3 decimals,
mute - actually differs from what was last published or signalled.

`volumePct` rounds away from zero rather than to even, so 12.5 and 37.5 percent do not round in
opposite directions at neighbouring steps of the same slider.

**Measured** (JOES-PC, AOT, 2026-09-22): a refresh costs 1.6-1.8 ms (the default-device check;
13.9-14.3 ms for the first in a cold process); a volume or mute change reaches the wallpaper in
459-478 ms, 400 of it the bus's coalescing window; a 6 s slider drag of 64 changes cost 15
repaints at about 64 ms CPU each; at rest the callback is silent and adds no wake. The source
costs +31 handles (CoreAudio's enumerator, endpoint, volume object and its RPC connection), +1
thread and +1.4 MB private bytes.

**Measured, the device notifier** (JOES-PC, 2026-10-04; in-process, `Process.HandleCount` and
thread count after a GC and a 3 s settle, six fresh processes, the same deltas every time):
registering the `IMMNotificationClient` on top of a live volume source adds **+3 handles and +1
thread** (the audio service's notification thread). The other way round, notifier first, it is
+25 handles / +1 thread and the volume source then adds +19 / 0, so the whole source is +44
handles / +1 thread at rest either way. Disposing returns none of them: CoreAudio keeps its RPC
plumbing for the life of the process. Over 60 s at rest the process spent 15.6 ms CPU with the
notifier and 46.9 ms without, both at the timer's granularity, so the new thread costs no
measurable idle CPU. `Start` costs 7.7-25 ms once per process (it is the first CoreAudio call,
made before the first reading) and 0.5 us on every later refresh. `tick --measure` on an
audio-only layout, three runs each, JIT: resolve 65-109 ms on `main`, 51-74 ms with the notifier,
inside run-to-run noise; native AOT: resolve 13-16 ms, total 138-152 ms.

**How the device path was verified.** The owner's audio device cannot be changed from a test, so
the default-change logic (which notifications count, the pending flag, the re-registration, the
empty record) is unit-tested with an injected `IAudioDeviceNotifier`; the real notifier is checked
to register with and unregister from CoreAudio on this machine (skipped where Audiosrv is
stopped); and both hand-built vtables are read back and every slot checked to hold the callback
named for it, in the slot order of CsWin32's generated `IMMNotificationClient.Vtbl` and
`IAudioEndpointVolumeCallback.Vtbl` (swapping two slots fails the test). Not yet observed: a real
headset plug on JOES-PC end to end.

**Implementation notes.** `IAudioEndpointVolumeCallback` and `IMMNotificationClient` are
implemented with hand-built vtables (`ComCallback`): `[UnmanagedCallersOnly(CallConvs =
[typeof(CallConvStdcall)])]` statics over a struct whose first field is the vtable pointer, with a
`GCHandle` back to the owner and the shared IUnknown slots. Nothing escapes the
callback - it runs on an audio service thread inside native code's own call frame, where an
exception crossing back takes the whole process down - and it calls nothing back into COM. The
same holds for the device notifier's callbacks. The
reader keeps two locks: one for the COM pointers, one for the four fields the notification writes,
never nested, because `UnregisterControlChangeNotify` can block on a notification already in
flight. COM is touched on the first refresh, not at construction. `Dispose` unregisters, releases
and frees the GCHandle, and is safe twice.

The endpoint role is `eMultimedia`: that is what the Windows volume flyout and the keyboard volume
keys move, so it is the number the screen should agree with.

## `command`

Settings: `command` (required), `args` (optional, may contain `{secret:name}`), `workingDir`
(optional), `every` (default 600 s), `timeout` (seconds, default 10), `parse` (`"json"` or
`"text"`; default: `json` if stdout starts with `{` or `[`, else `text`), `unixTimeFields`
(comma-separated field names to reinterpret as Unix timestamps when parsing JSON).

`command` and `workingDir` are expanded once, when the layout is loaded: `%ENV%` variables and
then the `runtime:` prefix (below). `args` is not a path and is not expanded; its `{secret:}`
substitution happens per request instead.

Runs the command hidden (no window), captures stdout as UTF-8. Publishes `text` or `json`
(whichever `parse` picked), `exitCode` (`NumberValue`), `ranAt` (`TimeValue`), and `stderr`
(`TextValue`) only when stderr is non-empty.

- A non-zero exit that printed **something** to stdout is still a success (scripts use the exit
  code as a flag the layout can bind to); a non-zero exit with **empty** stdout throws, so the
  last good values stay published.
- A timeout kills the whole process tree and throws.
- **A poll is on the tick's critical path**: the refresh waits for the process to exit before the
  frame resolves. Measured: about 27 ms per run for a `.cmd` script, 1.3-1.5 s for a
  `powershell.exe` recipe (Playnite, Tailscale). Due refreshes run side by side, so two such
  recipes cost the slower one, and only when they are due or on a forced tick.
- **Secrets in the output are redacted.** A command that fails and echoes its own argument list
  back (the usual shape of a usage error) would otherwise publish the *substituted* value of a
  `{secret:...}` in `args`. Every string the source publishes -- `stderr`, `text`, and every
  string inside `json` -- has each value its `args` substituted put back as its `{secret:name}`
  placeholder before it is published (`Secrets.Redact`), so a value, the designer's source panel
  and the wallpaper see the template, as the `http` source's errors do. What it cannot catch: a
  secret the command re-encodes before printing (URL-escaped, base64, JSON-escaped, split across
  lines), and a number that happens to be one, since only strings are redacted. Only the secrets
  this source's `args` names are redacted; text that matches some other secret is left alone.

### `stream: true`: a command that stays up and pushes

Add `"stream": "true"` to the settings and the source changes shape entirely. The process starts
on the first refresh and **stays up**; every line it writes to stdout is parsed the way the
non-streaming source parses its whole output, becomes the source's current values, and wakes the
daemon. This is how a cheap always-on producer works without a process spawn per tick, and it is
the seam for anything that can print a line -- native AOT cannot load a plugin, so printing is
the way in for a producer you write yourself. The other way in is the event pipe, below; use the
pipe when your producer wants to own its own lifetime, and `stream` when you would rather
DeskWall started and restarted it for you.

A line is expected to be one whole record: `{ "temp": 41.2 }` in `json` mode, or any text in
`text` mode. Print one line per change and nothing else.

Publishes `json` or `text` (the last line that parsed), `ranAt` (`TimeValue`, when that line
arrived), `running` (`BoolValue`), `starts` (`NumberValue`, how many times the process has been
started, so a crash loop is visible on the wallpaper), `badLines` (`NumberValue`) and `stderr`
(the last non-empty stderr line) when there is one. Lines and `stderr` are redacted as above.

- **The parse mode is fixed once**, by `parse` if it is set and otherwise by the first line. It
  is not re-decided per line: one diagnostic line would otherwise move a producer's values from
  `json` to `text` and blank every binding under it.
- **A line that does not parse is skipped and counted** in `badLines`. The last good values are
  left alone, so a producer that prints a stray line, or half a line because it was killed
  mid-write, does not blank the widget.
- **Nothing printed yet is not a failure.** Before the first line the record has `running`,
  `starts` and `badLines` and no payload, and bound components fall back. It deliberately does
  not throw: a failed refresh puts a source on the scheduler's back-off, and a backed-off source
  is refreshed on its back-off schedule rather than when it says it is due -- so the wake the
  first line raises would find the source not due and the push path would never start.
- **The process exiting is not fatal.** It is restarted with the same exponential back-off a
  failing source gets (250 ms, 500 ms, 1 s ... capped at 15 minutes), and a run that lasted 30
  seconds or more resets the doubling. A command that exits immediately therefore costs a few
  starts a minute, not a spin.
- **`timeout` does not apply** and is ignored, because there is no single run to time out. Bound
  the producer's own work inside the producer.
- `every` (default 600 s) is only the re-check that notices the process is down; it is not the
  latency, which is the line arriving plus the bus's 400 ms coalescing window.
- `Dispose` -- a layout change, a display change, shutdown -- kills the whole process tree, so a
  producer that starts a helper of its own does not leave it behind.
- **Cost at rest** (JOES-PC, AOT, four minutes against the same command polled at `every: 300`):
  +7 handles and +2 threads (the process handle, two redirected pipes, the stdout reader), 16 ms
  of CPU outside ticks (one 15.6 ms quantum), and no wakes of its own beyond the lines it prints.
  The trade is the producer's own resident process (8.5 MB for a `cmd.exe` loop), against a poll's
  spawn on the tick path (above).

```json
{
  "name": "gpu",
  "type": "command",
  "settings": {
    "command": "runtime:scripts\\gpu-watch.ps1",
    "command_comment": "prints one JSON object per change, forever",
    "stream": "true",
    "parse": "json"
  }
}
```

## `http`

Settings: `url` (required, may contain `{secret:name}`), `every` (default 600 s), `timeout`
(seconds, default 10), `header.<Name>` = value (one setting per header, value may contain
secrets), `parse` (`"json"` or `"text"`; default: `json` if the response `Content-Type` says so,
else `text`), `unixTimeFields`.

Publishes `json` or `text`, `status` (`NumberValue`, the HTTP status), `fetchedAt` (`TimeValue`),
`fromCache` (`BoolValue`: true when a `304 Not Modified` served the previous body).

- Revalidates with `If-None-Match` / `If-Modified-Since` when a previous fetch supplied an ETag
  or `Last-Modified`.
- The response body is capped at 4 MB (`BoundedHttp`); a larger body fails the fetch rather than
  buffering without limit.
- A hard ceiling of six times the source's own `timeout` bounds one fetch end to end
  (`HardCeiling`). The token `AsyncSource` hands the fetch is never cancelled on its own -- the
  work must finish and report so the next tick can use the result -- so without this ceiling a
  server that completes the TCP handshake and then stalls the body never trips the connect
  timeout, and the source fails every tick for the life of the daemon instead of eventually
  giving up and retrying clean.

## `rss`

Settings: `url` (required), `every` (default 900 s), `timeout` (seconds, default 10), `max`
(items, default 20).

Accepts RSS 2.0 (`<rss><channel><item>`, `pubDate`) and Atom 1.0 (`<feed><entry>`,
`published`/`updated`, `<link rel="alternate">` preferred over other link relations). Publishes
`title`, `link` (the feed's own), and `items`: a list keyed by `link`, each item `title`, `link`,
`published` (`TimeValue`, omitted if unparsable), `summary` (HTML tags stripped, collapsed
whitespace, truncated to 500 characters), `author` (omitted if absent).

Same body cap (4 MB) and hard ceiling (`timeout * 6`) as `http`, for the same reason.

## Paths in a source: `%ENV%` and `runtime:`

A `command` source's `command` and `workingDir` and a `file` source's `path` take the same two
expansions an image's `source` takes (`Paths.ExpandPath`, one implementation so the four cannot
drift apart):

| Written | Becomes |
|---|---|
| `%LOCALAPPDATA%\Foo\x.ps1` | the environment variable, as `ExpandEnvironmentVariables` does it |
| `runtime:scripts\progress.ps1` | `<runtime dir>\scripts\progress.ps1`, honouring `DESKWALL_HOME` |
| `runtime:` | the runtime dir itself |
| anything else | itself, untouched |

`runtime:` is case-insensitive and accepts forward slashes. It exists so a committed layout or a
shared widget never carries one machine's `C:\Users\<name>\AppData\Local\DeskWall\...` inside
it. Expansion happens at construction, so a variable changed after the daemon started is not
picked up until the layout is reloaded.

## `file`

Settings: `path` (required; `%ENV%` variables and `runtime:` expanded, see above), `parse` (`"json"`, `"text"` or `"rss"`;
default by extension: `.json` -> json, `.xml`/`.rss`/`.atom` -> rss, else text), `every` (default
300 s -- a cheap mtime re-check, not the latency; see the watcher below), `unixTimeFields` (as
`http`).

Publishes `json`, `text`, or (for `"rss"`) the same `title`/`link`/`items` shape as the `rss`
source, plus `modifiedAt` (`TimeValue`, local time), `size` (bytes), `exists` (`BoolValue`,
always `true` when this source has ever published -- see below).

- **The source watches the file** (a `FileSystemWatcher` on its directory, filtered to its name)
  and signals the daemon when it changes, so a save reaches the wallpaper in about a second
  rather than at the next re-check. Several file-system events for one save (an editor writing,
  or writing a temp file and renaming it over the target) are collapsed by a 300 ms debounce into
  one signal, and a burst of signals across several sources costs one repaint, not one each.
  One save raises two file-system events on JOES-PC, three or four for a save-by-rename. Measured
  save-to-wallpaper: 759-793 ms, median 766 (300 ms debounce, 400 ms bus window, ~66 ms of tick).
- Because the watcher carries the latency, `every` defaults to **300 s** (it was 30 s while the
  mtime poll was the only path). It is now purely a re-check, and it stays because a watcher is
  not guaranteed: a network path or a container mount can raise no events at all, and a directory
  that does not exist yet cannot be watched until it does (the source retries on each refresh).
- `NextDue` returns "now" whenever the watcher has seen a change that has not been published yet,
  or the file's mtime has changed since the last refresh, so any daemon wake -- the watcher's or
  anything else's -- picks up an edit rather than waiting out the re-check. Both, rather than
  mtime alone, because NTFS timestamps are coarse enough that a save in the same tick as the
  previous refresh can look unchanged.
- **The retry after a failure is still 30 s**, not 300. While a source is failing the scheduler
  refreshes it on its back-off and ignores `NextDue`, so the watcher cannot help it; a producer
  that deletes its file and writes the new one more than a debounce later would otherwise hold
  its last values for five minutes. Setting `every` below 30 s lowers the retry too.
- **A missing file throws** rather than publishing `{ exists: false }`. Publishing that shape
  would be a *successful* record with false-y content that overwrites the last good list -- the
  same partial-record-over-last-good problem spec 3.2 rules out elsewhere. A producer that writes
  its output by delete-then-create would otherwise blank the bound component for one tick, every
  time it ran. A layout that wants to show "the file is missing" does that through the bound
  component's own fallback, not through a value this source publishes.

This is the seam Hearth (the owner's Playnite add-on) is expected to feed through: Hearth writes
a JSON or RSS-shaped file, a `file` source in the layout reads it, no Playnite- or LiteDB-specific
code exists in DeskWall itself (spec 4.3).

### Recipe: Hearth recently-played games

Hearth writes `%LOCALAPPDATA%\DeskWall\feeds\hearth-recent.json` (atomically, temp + rename) on
any game start/stop or library change -- the 8 most recent installed, non-hidden games, Steam
sessions merged from `localconfig.vdf` the way `poc/data.ps1` does it, because Playnite never
records those itself. Full feed schema, the exact merge rule and error handling:
`docs/hearth-feed.md`.

```json
{ "name": "hearth", "type": "file", "every": 300,
  "settings": { "path": "runtime:feeds/hearth-recent.json", "parse": "json" } }
```

`every` only bounds the retry after a failure (30 s floor, above); the watcher makes an edit
reach the wallpaper in about a second regardless. Publishes `hearth.json.games`, a list of at
most 8 items (`id`, `name`, `lastPlayed`, `cover`, `installed`, `playtimeMinutes`, `launch`) plus
`hearth.json.writtenAt`. Before Hearth has ever run, the file does not exist and the source
throws -- same as any other `file` source, spec 3.2 -- so a bound component draws its own
fallback rather than a blank list. The `recent-games` widget (`layouts/widgets/recent-games.json`,
`layouts/README.md` "Recent games (Hearth)") is this recipe already wired to a repeater of covers
and shortcuts; use it directly rather than re-declaring the source by hand.

## Remote images

An `image` component (or a repeater template's image child) may bind `source` to an `http(s)`
URL. The render path only ever opens **local** files, so `RemoteImageCache`
(`src/DeskWall.Core/Render/RemoteImageCache.cs`) sits in between:

- `Lookup(url)` is synchronous and never touches the network. A hit returns the cached file path
  immediately; a miss returns `null` (the renderer draws the same fallback plate as a missing
  local file) and starts a background download.
- Cache files live at `runtime/images/<sha256-16-of-url>.img`, alongside a `.meta` file (URL,
  ETag, fetch time) used to revalidate.
- A cached file older than `maxAge` (default 24 hours) is still served immediately while a
  revalidation (conditional `GET`) runs in the background.
- One download per URL at a time (`_inFlight`); a URL that has failed recently is skipped for 10
  minutes (`NegativeTtl`) rather than retried every tick.
- A download is capped at 20 MB and must report an `image/*` content type, or it is treated as a
  failure (and negative-cached).
- When a download lands, `Landed` fires and the daemon posts a wake so the frame gets repainted
  with the real image on the next tick.
- Files nobody has looked up for 30 days are deleted by `Sweep()`, which the daemon runs once at
  startup. A lookup sets the file's last-access time explicitly, so this works with NTFS
  last-access updates disabled.

## Secrets

`secrets.json` in the runtime dir is a flat `{ "name": "value", ... }` map
(`src/DeskWall.Core/Sources/Secrets.cs`). A layout never contains a secret directly; it
references one by name with `{secret:name}` inside an `http` source's `url` or `header.*`
values, or a `command` source's `args`. Substitution happens at request time
(`Secrets.Substitute`) and the resolved value is never logged -- a failed request logs the
template (`.../{secret:steamKey}/...`), not the key. What comes back is redacted the other way
(`Secrets.Redact`): every string an `http`, `rss` or `command` source publishes has the values its
own templates substituted replaced by their `{secret:name}` placeholders, so a response or a
command output that echoes the request cannot carry a secret into a value. Source values never
reach `events.json` or the event bus's diagnostics ring (both hold only what producers push
through the pipe), and a source's failure reaches `SourceSnapshot.LastError` (the designer's
source panel) only as its exception message: DeskWall's own messages name the template or the
command, and .NET's (a refused connection, a process that cannot start) name the host or the
program, never the query string or the arguments. An unknown secret name throws
`KeyNotFoundException` naming the secret, never a value. The daemon and `deskwall tick` never
write this file; the owner (or the designer's secrets editor) does.

## Pushed values: events

A source is *pulled*: the layout tells the daemon what to go and do, and the scheduler decides
when. An **event** is the other direction. Any program running as you writes one JSON line to
`\\.\pipe\DeskWall.Events` and the values in it appear in the value tree at once, under a name
nothing had to declare.

```json
{"source":"build","data":{"status":"green","failures":0}}
```

A layout binds `build.data.status` and it resolves. There is **no event source type** and nothing
goes in the `sources` array: a source declaration exists to say what to go and do, and a pushed
provider needs none of that. A binding to a provider that has never sent anything falls back like
any other unresolvable binding.

**The renderer draws state, not moments.** An event is a patch to the value tree plus a request to
repaint, never something drawn for a while and then removed: there are no transient displays, no
component lifetimes, no un-draw timers. And **subscription is the binding graph**: a component
bound to `build.data.status` is subscribed by definition, and content keys already redraw only
what changed, so there is deliberately no register/unsubscribe API to drift from it. The bus
lives in Core (the designer runs its own); the pipe belongs to the daemon, because two processes
cannot own one pipe name.

### The envelope

CloudEvents attribute names, without the conformance. Unknown attributes (`specversion`,
`datacontenttype`, anything else) are ignored, never a reason to reject.

| Attribute | Required | Use |
|---|---|---|
| `source` | yes | Which provider this patches. Must be a binding name: a letter or `_`, then letters, digits, `_` or `-`. |
| `data` | yes | An object. Merged into that provider's `data` record. |
| `type` | no | What happened. Recorded and bindable. Does not route. |
| `subject` | no | Recorded and bindable. Does not route. |
| `id` | no | Recorded. An event repeating the previous `id` is dropped, so a heartbeat costs no repaint. |
| `time` | no | The producer's timestamp, published as `sentAt`. Unparseable means absent, not rejected. |
| `replace` | no | `true` replaces the `data` record instead of merging into it. |
| `wake` | no | `false` updates the value without waking the daemon; it appears at the next ordinary tick. |

**Events patch, scheduled refreshes replace.** Sending `{"level":0.2}` after
`{"level":0.4,"device":"Speakers"}` leaves `device` alone: a producer sends what changed and must
not blank what it does not know. The merge is top level only, so `{"a":{"y":2}}` after
`{"a":{"x":1}}` leaves `a` as `{"y":2}`. `replace: true` asks for the whole record to be swapped.

### What a provider publishes

| Field | Type | Notes |
|---|---|---|
| `data` | record | The merged payload. Bindings read `build.data.status`. |
| `type`, `subject`, `id` | text | From the last event; the key is absent when the attribute was. |
| `sentAt` | time | The producer's `time`, absent when it sent none. |
| `receivedAt` | time | When the bus accepted it. |
| `ageSeconds` | number | Whole seconds since `receivedAt`, recomputed at every refresh. |

A component bound to `ageSeconds` therefore redraws every minute by design, which is what lets a
layout grey out or hide a value whose producer has gone quiet. Nothing else in the record changes
unless an event arrives.

### Sending one

PowerShell, connect-write-disconnect:

```powershell
$p = New-Object IO.Pipes.NamedPipeClientStream '.', 'DeskWall.Events', 'Out'
$p.Connect(2000); $w = New-Object IO.StreamWriter $p; $w.AutoFlush = $true
$w.WriteLine('{"source":"build","data":{"status":"green"}}')
$p.Dispose()
```

A shell, for one line and nothing more (`cmd`, and so any `.bat`):

```bat
echo {"source":"build","data":{"status":"green"}} > \\.\pipe\DeskWall.Events
```

**Which pipe.** `DeskWall.Events` belongs to the daemon on the default runtime dir
(`%LOCALAPPDATA%\DeskWall`), which is the installed one. A daemon on any other home (`--home` or
`DESKWALL_HOME`) listens on `DeskWall.Events.<hash>` instead -- the same suffix as its
single-instance lock (`RuntimeInstance.EventPipeName`) -- so a scratch daemon never shares the live
one's pipe, and a producer always reaches the daemon it meant. A producer for such a home asks:

```powershell
$name = & deskwall.exe --home C:\scratch\home pipe | Out-String   # DeskWall.Events.8E76E78989D2DA7A
$p = New-Object IO.Pipes.NamedPipeClientStream '.', $name.Trim(), 'Out'
```

(`deskwall` is a WinExe, so `& ... | Out-String` is what makes PowerShell wait for its output.)
`deskwall pipe` with no `--home` prints `DeskWall.Events` unless `DESKWALL_HOME` is set.

A producer may also hold the connection open and write a line whenever something changes; up to
four producers can be connected at once. Blank lines are ignored, so a trailing newline is free.

Repaints are coalesced: the bus wakes the daemon at most every 400 ms, with a trailing wake so the
final state always lands. Fifty events sent in a burst cost one repaint, and fifty spread over a
two-second slider drag cost about six.

Measured (JOES-PC, AOT, 2026-09-21): one connect-write-disconnect event reaches a new wallpaper
on disk in about 480 ms -- 12 ms for the client, the 400 ms window, about 60 ms of tick. An idle
daemon with providers in the registry still wakes only on the minute. The seam itself costs about
+35 to +39 handles and one thread at rest: `EventPipeServer` is one synchronous listener thread
blocked in `ConnectNamedPipe` (synchronous on purpose; CLAUDE.md "A named-pipe client can beat
`ConnectNamedPipe`"), plus a second only while a producer is connected. It is its own thread,
not a pool work item, so an event never queues behind a render.

### Remembering, and forgetting

Every provider's last record is written to `%LOCALAPPDATA%\DeskWall\events.json`, at most every
few seconds and once on shutdown, and restored at start. That is what makes a pushed widget
survive a sign-in, and it is also what the designer reads: its providers panel watches that file,
so a producer started while the designer is open appears without a restart, with every field it
has ever sent offered to the binding picker.

Because events merge, the remembered record accumulates every field a producer has ever sent, not
only the ones in its latest event. Nothing expires on its own; the designer's **Forget** drops a
record, and that holds while the daemon runs. The daemon watches `events.json` too, and reads it
back after any change and before each of its own saves: a provider it last wrote that is now
missing from the file has been forgotten, and it drops its own copy and repaints without it. A
provider that arrived since the daemon's last save is not missing, only not yet written, and is
kept; so is one whose producer sent again after the Forget was written. A file that is missing or
cannot be parsed forgets nothing. What Forget cannot do is stop the producer: one that sends again
is remembered again.

### Describing a provider

`providers/<name>.json`, loaded from the directory beside `deskwall.exe` and then from
`%LOCALAPPDATA%\DeskWall\providers`, the later winning on the same name. A manifest is only for
what observation cannot supply: a provider that has never run here, per-field descriptions and
examples so the binding picker reads as prose, and `expectEverySeconds` (carried and shown,
not enforced: #32). The designer's **Describe** writes one seeded from the
observed fields.

```json
{
  "version": 1,
  "name": "build",
  "description": "The CI light for whatever is checked out.",
  "expectEverySeconds": 900,
  "fields": [
    { "path": "data.status", "type": "text", "example": "green", "description": "green, amber or red" }
  ]
}
```

Where a manifest and an observed record disagree about a field, the observed value wins for
rendering and the manifest wins for describing.

### Collisions and diagnostics

A layout source and a pushed provider with the same name are **not** merged: the layout source
wins the name outright, from its first tick, and the daemon logs one WARN naming the clash.

Every line the pipe offers is logged. A rejected line is one INFO with the reason the parser gave
it (`missing "source"`, `invalid json: ...`, `"source" "my build" is not a valid binding name`,
`duplicate id "7"`), and the first event from a provider logs `events: provider 'x' seen`.
Between those lines and the tick line that follows, "my script sends events and nothing happens"
can be told apart from "the daemon is not running" and from "nothing is bound to that value".

### Trust

The pipe's ACL grants exactly one account: the user the daemon runs as. Nothing else, not SYSTEM
and not an administrator, is granted access by omission. The threat left is a process already
running as you, and event values are used exactly like any other source value -- which means a
layout that binds a `shortcut` target to one will launch whatever it says. That is the same trust
an `http` source already has, and it is the layout author's choice, but make it deliberately.

## `battery`

Windows GetSystemPowerStatus, every 60 seconds by default, on whole multiples of `every` since
midnight (like `hardware`) so it shares the clock's wake instead of adding one at whatever second
the daemon started. Publishes `percent` (0..100),
`fraction` (0..1), `charging`, `onBattery`, and `minutesLeft` (-1 when unknown).
No battery or unknown charge omits percent/fraction. `opacity` is 0.2 when on mains
and at least 95% charged, otherwise 1; absent for unknown charge.

Hardware also publishes `cpuHistory`, `ramHistory`, and `gpuHistory` when a GPU reader exists:
oldest-first lists of `{ "v": fraction }`, rounded to three decimals. Empty until sampled.

## `media`

Windows global media session, event-driven (`ISignalSource`), no polling. Publishes `title`,
`artist`, `album`, `playing`, `app`, and `art` (`ImageValue`), plus `position` and `duration`
(seconds) and `progress` (0..1, position over duration; 0 when the app reports no duration).
Position is extrapolated from the timeline's last update while playing. The source still only
refreshes on events: a track change, play/pause, a seek of more than 3 s, or a changed end time
(`TimelinePropertiesChanged` fires every few seconds on some apps, so smaller moves are
ignored). A progress bar therefore jumps on those events rather than creeping every minute.
Paused or absent playback has empty display fields. Thumbnail bytes are hashed; only changed art is decoded to PNG under
`runtime/media/art.png`, through temp files unique to the call (the designer's own media source
shares the runtime dir). Thumbnails above 8 MB are omitted, and a thumbnail that will not open,
read or decode publishes `art` as "" rather than failing the source. Subscriptions are removed on disposal.
Every call into the media service is bounded at 5 s (`MediaSource.CallTimeout`) and by the tick's
token: the daemon's tick waits on this refresh, so a service that stops answering fails the source
(last values kept, retried on the scheduler's back-off) rather than freezing the wallpaper.
Windows SDK projections compiled successfully under native AOT in the spike.

`disks.worstUsedFraction` is the maximum used fraction across fixed ready drives (0 when none).

## `notifications`

Settings: `include`, `exclude` (optional, comma-separated app names; each entry matches the app's
display name *or* its app user model id, case-insensitive, exactly). `every` (default 300 s) is
the sweep interval described below, not a poll.

What is waiting in the Windows notification centre (toasts only), through WinRT's
`UserNotificationListener`. Push, like `audio` and `media`: an `ISignalSource` that tells the bus
when what it publishes changed. Zero-config, so it is one of the designer's built-in sources.

| Field | Meaning |
|---|---|
| `status` | `ok`, `denied` (Settings > Privacy & security > Notifications does not let desktop apps read them), or `unavailable` (the listener could not be reached). When not `ok` it is the **only** field, so every bound property falls back to its own default. |
| `count` | toasts in the centre after the filters |
| `apps` | list of `{ name, count }`, keyed by `name` (`notifications.apps[Outlook].count`), most toasts first, ties most-recent first |
| `latestApp`, `latestTitle`, `latestText` | the newest toast: app display name, its first text line, the remaining lines joined with a space. Empty strings when the centre is empty. |
| `latestAt` | `TimeValue`, when the newest toast was raised; absent when the centre is empty |
| `changedAt` | `TimeValue`, when the published set last changed -- an arrival *or* a dismissal. Stable between changes, so it does not dirty the content key. |

```json
{ "name": "notifications", "type": "notifications",
  "settings": { "exclude": "Windows Security, Microsoft Store" } }
```

A badge: `{notifications.count}` in a text part, or a repeater over `notifications.apps` showing
`{name} {count}`.

**Access.** `GetAccessStatus` is asked on the first refresh, and `RequestAccessAsync` only while it
answers "unspecified". From an unpackaged process neither prompts: both answer from the privacy
setting. Measured on JOES-XPS-17: `Allowed` with the setting at its default. A `denied` source is
never due again; after changing the setting, reload the layout or restart the daemon.

**Being told.** The brief asked for `NotificationChanged`. It is subscribed first, but it throws
`0x80070490` (element not found) in a process without package identity, which both executables
are. So the reader watches what the notification platform itself writes:
`%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db*`. Measured: silent at idle (0 events
in 10 s), about 30 events for one toast arriving. The platform also writes that store about ten
times per toast returned **on every read** -- anyone's read -- so a watcher that reads on every
event feeds itself, and two processes running the source (the daemon and the designer's Data
panel) would feed each other. Reads therefore go through a session-wide gate, a 24-byte named
mapping `Local\DeskWall.NotificationReads` (`SharedReadGate`): readers in flight, a 10 s cap on
believing one is in flight (a reader that died mid-read must not blind everybody), and the time
the last read's echo is over (750 ms after it ends). Store events inside that window are dropped.

**The read.** A burst of events is debounced (300 ms) into one background read, off the tick
thread; a reading that differs from the published one sets the pending flag and signals the bus,
exactly like `audio`. One reading that matches is not a change, so badge and tile writes, and
other processes' reads, cost a read and nothing more. Because a toast that lands inside a read's
suppressed echo would otherwise wait, every event-driven read is followed by one confirming read
2 s later (never by another). Cost, measured on JOES-XPS-17 with 3-4 toasts: 280-540 ms wall and
60-230 ms CPU per read (the first in a process is the dearest), almost all of it inside the
notification service's own call. The latency from `Show()` to the bus signal was about 1.4 s.

**The sweep.** The store watcher sees every arrival but not every dismissal: in one run a
`History.Clear` wrote the WAL within a second, in another it wrote nothing until the next read,
because NTFS updates a file's last-write metadata lazily while its writer holds it open. So while
the published `count` is above zero, the source is also due every `every` seconds (rounded up to
the whole minute, so it rides the clock's wake) and reads inline on that tick. An empty centre
has nothing to dismiss and is never swept; a `denied` one never either. Worst case, a dismissal
takes `every` to disappear from the wallpaper.

Known blind spot: a toast whose writes all land inside the echo window of a read the source did not
follow up -- a confirming read, a sweep, or the other process's read -- is not seen until the next
change, or the next sweep if the centre was not empty (#40).

## Playnite recent games recipe

`scripts/playnite-recent.ps1` runs under Windows PowerShell 5.1 with Playnite's own LiteDB.
It copies games.db to a uniquely named temporary file, opens only that copy read-only, selects
the four most recently played installed/non-hidden games and publishes `games`, `left`, `right`.
Each record has id, name, ISO last activity, absolute cover path and quoted executable target.
A locked copy uses the last successful runtime cache. No installation/cache emits empty lists
and status `unavailable`, with diagnostics on stderr. It never closes Playnite. Portable installs
can pass `-InstallDir` and `-LibraryDir`. Cache writes honour DESKWALL_HOME.

The alpine layout uses `scripts/tailscale-lights.ps1` as a five-minute command recipe. It emits
`peers` containing one record per online peer while the backend is Running, otherwise an empty
list, plus `status` and `count` (the number of online peers, so a text can say "3 peers online"
without counting the list). The layout draws a light per online peer from that list.

## Calendar recipe

`scripts/graph-next-event.ps1` (PowerShell 7) reads the signed-in account's calendar from
Microsoft Graph (`/me/calendarView`) and prints the next three events in the coming 12 hours as
one line of JSON. Copy it to `%LOCALAPPDATA%\DeskWall\scripts\` (the publish script does not ship
`scripts/`), then add the source:

```json
{ "name": "calendar", "type": "command", "every": 300,
  "settings": { "command": "pwsh.exe",
                "args": "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File graph-next-event.ps1",
                "workingDir": "runtime:scripts", "parse": "json", "timeout": "30",
                "unixTimeFields": "start,end" } }
```

`timeout` is 30 rather than the default 10: loading the Graph module and refreshing the token
takes 3 to 4 s on a warm machine. `unixTimeFields` makes `start`/`end` `TimeValue`s, so they take
a date format. It is not a default source: it needs a sign-in, and a Microsoft 365 or Outlook.com
account.

Output (`calendar.json...`):

| Field | Notes |
|---|---|
| `status` | `ok`, or `signin-required` (exit code 2, empty `events`) when there is no usable token |
| `account` | The signed-in user principal name |
| `count` | Events in `events`, 0..3 |
| `events` | The list: `subject`, `start`, `end`, `startText`/`endText` (local `HH:mm`), `minutesUntil` (whole minutes; negative once started), `inProgress`, `isOnline`, `joinUrl`, `location`, `isAllDay` |
| `next` | `events[0]`; absent when there is nothing in the window |

Cancelled events are skipped, and so are all-day events unless `-IncludeAllDay` is added to the
arguments. `-Count N` (1..20) and `-Hours N` (1..168) change the window. Any other failure
(network, Graph) exits 1 and prints nothing, so the source keeps the last good events.

Bindings: `calendar.json.next.subject`, `calendar.json.next.start | HH:mm`,
`calendar.json.next.minutesUntil`, `calendar.json.next.isOnline | "?true=online"`, a repeater over
`calendar.json.events`, and `calendar.json.status | "?signin-required=calendar: sign in"` so the
layout says when the token has lapsed. `minutesUntil` is as old as the last run (up to five
minutes); for a live countdown, compare `start` with `time.now` instead.

**Signing in.** The script never prompts when it runs as a source, so sign in once:

```powershell
deskwall calendar login                    # Windows sign-in (WAM); the default
deskwall calendar login -Flow devicecode   # device code, token kept by the script
```

Either one opens a PowerShell 7 window of its own, runs the script with `-Login`, and waits.
Arguments after `login` go to the script unchanged, and any `-Flow`/`-Tenant` given there must be
added to the source's `args` as well. The two flows:

- `-Flow mg` (default) uses the `Microsoft.Graph.Authentication` module
  (`Install-Module Microsoft.Graph.Authentication -Scope CurrentUser`) and its token cache. It
  shares both with the owner's ms-todo skill. Afterwards `Connect-MgGraph` refreshes the token
  silently. This is the flow for a work tenant with device-compliance Conditional Access: WAM
  carries the device's state, and a device code cannot.
- `-Flow devicecode` signs in with a device code against the public Microsoft Graph PowerShell
  client (`14d82eec-204b-4c2f-b7e8-296a70dab67e`, scopes `Calendars.Read offline_access`). It keeps
  the refresh token in `<runtime dir>\calendar-token.txt`, DPAPI-protected for the current user,
  and saves every rotated token. Use it for a personal account (`-Tenant consumers`) or a tenant
  without that policy. It needs no module.

Without a visible console, WAM refuses at once ("A window handle must be configured"), which is
what the source wants. The script exits 2 and the layout shows `signin-required`. From a hidden
console window (Task Scheduler, `Start-Process -WindowStyle Hidden`), WAM would instead wait on a
prompt nobody can see. So the script detaches from a hidden console before it connects, and exits
1 there.
