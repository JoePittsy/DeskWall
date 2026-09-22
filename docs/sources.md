# Sources

A source is declared in a layout file's `sources` array:

```json
{ "name": "steam", "type": "http", "every": 600, "settings": { "url": "...", "unixTimeFields": "..." } }
```

| Field | Notes |
|---|---|
| `name` | The root field bindings use to reach this source's values (`steam.json...`). |
| `type` | One of `time`, `disks`, `system`, `hardware`, `command`, `http`, `rss`, `file` (`SourceFactory.Create`). Anything else throws when the layout is loaded. |
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

No settings. Always due on the next whole minute (`NextDue` rounds up to `:00`).

Publishes: `now` (`TimeValue`), `date` (`TextValue`, `yyyy-MM-dd`), `weekday` (`TextValue`, e.g.
`Saturday`), and how far through the day, week and year local time is:

| Field | Meaning |
|---|---|
| `dayFraction`, `weekFraction`, `yearFraction` | 0..1, rounded to 4 decimals |
| `dayPercent`, `weekPercent`, `yearPercent` | the same, 0..100, rounded to a whole number |

Both forms exist because a `bar`'s `fraction` wants 0..1 and a `text` wants the percent, and a
format string cannot multiply by 100. The **week starts on Monday** (`((int)DayOfWeek + 6) % 7`),
not on Sunday. The year divides by 366 in a leap year and 365 otherwise. All three are derived
from the same local `now` the clock publishes, so a "day progress" widget needs no script.

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
`System.Threading.Timer` inside the source, started on the first `RefreshAsync` and stopped on
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
- **Caveat:** `stderr` is published verbatim. A command that fails and echoes its own argument
  list back (the usual shape of a usage error) will publish the *substituted* value of any
  `{secret:...}` in `args` into a value a text component could draw on the wallpaper. Do not
  bind a component to a command's `stderr` if its `args` carry a secret.

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
- Because the watcher carries the latency, `every` defaults to **300 s** (it was 30 s while the
  mtime poll was the only path). It is now purely a re-check, and it stays because a watcher is
  not guaranteed: a network path or a container mount can raise no events at all, and a directory
  that does not exist yet cannot be watched until it does (the source retries on each refresh).
- `NextDue` returns "now" whenever the file's mtime has changed since the last refresh, so any
  daemon wake -- the watcher's or anything else's -- picks up an edit rather than waiting out the
  re-check.
- **A missing file throws** rather than publishing `{ exists: false }`. Publishing that shape
  would be a *successful* record with false-y content that overwrites the last good list -- the
  same partial-record-over-last-good problem spec 3.2 rules out elsewhere. A producer that writes
  its output by delete-then-create would otherwise blank the bound component for one tick, every
  time it ran. A layout that wants to show "the file is missing" does that through the bound
  component's own fallback, not through a value this source publishes.

This is the seam Hearth (the owner's Playnite add-on) is expected to feed through: Hearth writes
a JSON or RSS-shaped file, a `file` source in the layout reads it, no Playnite- or LiteDB-specific
code exists in DeskWall itself (spec 4.3).

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
  startup.

## Secrets

`secrets.json` in the runtime dir is a flat `{ "name": "value", ... }` map
(`src/DeskWall.Core/Sources/Secrets.cs`). A layout never contains a secret directly; it
references one by name with `{secret:name}` inside an `http` source's `url` or `header.*`
values, or a `command` source's `args`. Substitution happens at request time
(`Secrets.Substitute`) and the resolved value is never logged -- a failed request logs the
template (`.../{secret:steamKey}/...`), not the key. An unknown secret name throws
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

A producer may also hold the connection open and write a line whenever something changes; up to
four producers can be connected at once. Blank lines are ignored, so a trailing newline is free.

Repaints are coalesced: the bus wakes the daemon at most every 400 ms, with a trailing wake so the
final state always lands. Fifty events sent in a burst cost one repaint, and fifty spread over a
two-second slider drag cost about six.

### Remembering, and forgetting

Every provider's last record is written to `%LOCALAPPDATA%\DeskWall\events.json`, at most every
few seconds and once on shutdown, and restored at start. That is what makes a pushed widget
survive a sign-in, and it is also what the designer reads: its providers panel watches that file,
so a producer started while the designer is open appears without a restart, with every field it
has ever sent offered to the binding picker.

Because events merge, the remembered record accumulates every field a producer has ever sent, not
only the ones in its latest event. Nothing expires on its own; the designer's **Forget** drops a
record. (A running daemon holds its own copy and writes the file back on its next save, so a
Forget while the daemon is up is not durable yet.)

### Describing a provider

`providers/<name>.json`, loaded from the directory beside `deskwall.exe` and then from
`%LOCALAPPDATA%\DeskWall\providers`, the later winning on the same name. A manifest is only for
what observation cannot supply: a provider that has never run here, per-field descriptions and
examples so the binding picker reads as prose, and `expectEverySeconds` (carried and shown;
enforcing staleness from it is phase 2). The designer's **Describe** writes one seeded from the
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
