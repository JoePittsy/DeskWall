# One event seam: pushed values from anywhere

Date: 2026-09-21. Owner decisions taken in chat, listed in section 2.

## 1. Why

Everything DeskWall draws today is pulled: a source says when it is next due, the scheduler wakes,
the tick refreshes what is due. That is right for weather and disk space and wrong for anything
that changes on a human timescale, and it is why the volume widget built during the dogfood pass
had to spawn PowerShell every thirty seconds.

There are already five ad-hoc ways something out of band gets a repaint: the waitable timer, the
layout file watcher, the display-change wake, the image cache's `Landed`, and `AsyncSource`'s
`Completed`. A CoreAudio callback would be a sixth. The owner's objection is the right one: stop
adding bespoke paths, and make one seam that a user can reach as easily as we can.

That last part matters beyond tidiness. Native AOT cannot load plugins, so today a user can only
add *pull* providers (`http`, `command`, `file`). An event ingress is how they get push without
writing any of our code, which is the "self serve new providers" thread from 2026-09-21.

## 2. Decisions taken with the owner

| Question | Decision |
|---|---|
| What is the seam for | External producers first (scripts, Hearth, other apps). Built-in push sources use the same path. Existing internal wakes stay unless moving them is free. |
| Transient display | **State only.** Nothing appears briefly and vanishes. No component lifetimes, no un-draw timers. |
| Ingress | A **named pipe**, restricted to the current user. |
| After a daemon restart | **Remember the last value per source**, and publish an age the layout can show. No expiry timer in v1. |
| Sequencing | **Bus and pipe first**, with a script example to try. Volume follows as the first in-process producer. |

## 3. The model

**The renderer draws state, not moments.** An event is therefore a *patch to the value tree plus
a request to repaint*, never a thing that is itself drawn. Everything below follows from that.

**Subscription is the binding graph.** A component bound to `volume.data.level` is by definition
subscribed to it, and `ResolvedX` content keys already mean only the components whose value
actually changed are redrawn. There is deliberately **no register/unsubscribe API**: a second
table of who-depends-on-what would drift from the first. "Register your widget on an event" means
"bind a component to that source's values".

**Sources are declared, never conjured.** An event naming a source the layout has not declared is
logged, counted and dropped. Auto-creating branches would let any local process invent parts of
the value tree, and would leave the designer unable to say what is bindable before data arrives.

**Events patch; scheduled refreshes replace.** A volume-change event knows the level and not the
device name, and must not blank it. Merge is the default; an envelope flag asks for replacement.

## 4. The envelope

CloudEvents attribute names, without the conformance: accept the subset below, ignore unknown
attributes, never reject an event for missing `specversion`.

| Attribute | Required | Use |
|---|---|---|
| `source` | yes | Which declared source this patches. The only routing key in v1. |
| `data` | yes | An object. Merged into that source's `data` record. |
| `type` | no | What happened. Recorded, bindable, shown in diagnostics. Does not route in v1. |
| `subject` | no | Recorded and bindable. Does not route in v1. |
| `id` | no | Recorded. Used to drop an exact repeat of the previous event. |
| `time` | no | The producer's timestamp, published as `sentAt`. |
| `replace` | no | `true` replaces the `data` record instead of merging. |
| `wake` | no | `false` updates state without waking the daemon; the change appears at the next ordinary tick. |

Routing by `source` alone keeps the shape flat and predictable, and leaves `type` and `subject`
free to gain meaning later without breaking a producer.

## 5. The `event` source

A new source type, receive-only. `NextDue` is never due of its own accord; it publishes whatever
the bus last handed it.

Published fields, following the `http` source's convention of payload under a named key with
metadata beside it (`http` publishes `json`, `status`, `fetchedAt`):

| Field | Type | Notes |
|---|---|---|
| `data` | record | The merged payload. Bindings read `build.data.status`. |
| `type`, `subject`, `id` | text | From the last event; omitted when absent. |
| `sentAt` | time | The producer's `time`, omitted when absent. |
| `receivedAt` | time | When the bus accepted it. |
| `ageSeconds` | number | Computed at each refresh, so a layout can grey out or hide a stale value. |

`ageSeconds` changes every refresh, so a component bound to it redraws every minute by design, the
same as the clock. Nothing else in the record changes unless an event arrives.

Settings: none required. A source that has never received anything, and has nothing persisted,
publishes an empty record, and bound components fall back to their own defaults as they do for any
missing value.

## 6. Ingress: the pipe

`\\.\pipe\DeskWall.Events`, created by the daemon, ACL restricted to the current user. Newline
delimited JSON: one object per line, so a producer can connect, write and disconnect, or hold the
connection open and stream. Several producers at once are allowed.

A malformed line is logged with its reason and the connection continues; it never throws into the
listener. The listener is one thread blocked on a connect, which costs no CPU at rest, and this is
the one new thread the feature adds.

Documented one-liner clients for PowerShell and for a shell, in `docs/sources.md`, because a seam
nobody can reach has not shipped.

## 7. The bus

Lives in Core so the designer can run one too; the pipe listener lives in the daemon, because two
processes cannot own one pipe name.

- **Coalescing is the only performance-critical part.** A tick re-encodes and writes about two
  megabytes, so the bus wakes the daemon at most every 400 ms, with a trailing wake so the final
  state always lands. A burst of fifty events during a slider drag becomes a few repaints.
- **Waking is the daemon's existing `WakeKind.SourceCompleted`**, which every async source and the
  image cache already post. No change to `DaemonLoop`, the host window or the scheduler.
- **Diagnostics are not optional.** The bus keeps a ring of the last events, accepted and
  rejected, each with a reason. "My script sends events and nothing happens" has four causes
  (wrong source name, malformed envelope, daemon not running, nothing bound to the value) and
  without this the user cannot tell them apart.
- **Persistence**: the last record per source is written to `%LOCALAPPDATA%\DeskWall\events.json`,
  at most once every few seconds and once on shutdown, and restored at start. That is what makes a
  widget survive sign-in; `receivedAt` is what lets it admit its age.

## 8. In the designer

The event source's panel shows its current values, the recent-event ring, and a **Send test
event** button. Designing a widget against an event that has not happened yet would otherwise mean
binding blind, and the test path is also how a user checks their producer's shape.

## 9. Security and trust

The pipe is restricted to the current user, so the threat is a process already running as that
user. Event values are used exactly like any other source value, which means a layout that binds a
`shortcut` target to one will launch what it says. That is the same trust an `http` source already
has, and it is the layout author's choice, but it must be stated in the docs rather than
discovered.

## 10. What must be measured, not asserted

- Handles and threads before and after: the expectation is one blocked thread and a small number
  of handles, and the hardware source's NVML cost (37 MB, 190 handles, 3 threads) is the precedent
  for checking rather than assuming.
- An idle machine with an event source declared still wakes once a minute.
- A burst of events produces the expected small number of repaints, with the final state correct.
- The cost of one accepted event end to end, from write to wake.

## 11. Phase 2, sketched only

Volume becomes an in-process producer on the same bus: a CoreAudio
`IAudioEndpointVolumeCallback` whose notification carries the new level and mute flag, published
as a patch exactly as an external producer would. It needs a hand-built COM vtable under native
AOT, and a re-check of the default endpoint so plugging in a headset is noticed. Specified
properly when phase 1 has landed.

## 12. Out of scope

Transient or timed display; routing on `type` or `subject`; expiry of a remembered value;
a query or request-response channel (the pipe is one way); events from another machine; replacing
the five existing internal wake paths.
