# Plan: the six things the dogfood pass found

Context: `.superpowers/sdd/2026-09-21-widget-editor/progress.md` (dogfood entry) and the six
widgets under `.superpowers/sdd/2026-09-21-widget-editor/dogfood/`. Each item below is a thing
that stopped a real widget being built well, with the evidence that found it.

Rapid-dev phase: no reviewer agents. TDD for every Core change (test first, watch it fail).
Build stays at 0 warnings. Nothing touches the live runtime dir or the running daemon.

Base: `integration/2026-09-21-placement-and-text` @ 0b0ff7f or later.

## Lane A (Core): items 1, 2, 3, 5, and the Core half of 6

Branch `lane/source-and-format-fixes`. Files: `src/DeskWall.Core/Sources/TimeSource.cs`,
`Values/Value.cs`, `Sources/Hardware/HardwareSource.cs`, `Sources/CommandSource.cs`,
`Sources/FileSource.cs`, `Resolve/LayoutResolver.cs` (the `runtime:` helper, if it is the right
home for a shared one), `docs/sources.md`, `docs/layout-format.md` (binding grammar section only).
Tests in `tests/DeskWall.Core.Tests/`.

### A1. `time` publishes how far through the day, week and year we are

Evidence: building a "day progress" widget needed a PowerShell script spawned every minute for
three numbers the clock already knows.

Add to `TimeSource.RefreshAsync`: `dayFraction`, `weekFraction`, `yearFraction` (0..1, doubles
rounded to 4 dp) and `dayPercent`, `weekPercent`, `yearPercent` (0..100, rounded to a whole
number). Both forms because a `bar` wants the fraction and a `text` wants the percent, and a
format string cannot multiply by 100.

- The week starts **Monday** (`(int)DayOfWeek + 6) % 7`).
- The year divides by 365 or 366 by `DateTime.IsLeapYear`.
- Local time, from the injected `IClock`, same as `now`.

Tests: a fixed clock at midnight gives 0; at 18:00 gives 0.75 / 75; Monday 00:00 gives week 0;
Sunday 23:59 gives week just under 1; 1 January gives year ~0; 31 December 23:59 of a leap year
gives just under 1. No new `every`, no schedule change.

### A2. A bool (or any value) can pick a string: the map format

Evidence: the volume script publishes `muted` and nothing on screen could react to it.

Extend the binding format (`Value.ToText`) with a **map**: a format whose first character is `?`
is a comma-separated list of `key=text` pairs, with `*=text` as the fallback.

    volume.json.muted | "?true=#D13438,false=#EBFFFFFF"     -- a bool driving a colour
    volume.json.muted | "?true=muted"                       -- text when true, nothing when false
    weather.json.current.is_day | "?1=day,0=night,*=?"      -- a number driving text

Rules: the key is matched against the value's **own plain text** (`ToText(null)`),
case-insensitively, so `true`/`True`, `1`/`1`. No match and no `*` gives the **empty string**
(showing nothing is the point of the muted case). Keys and values cannot contain `,` or `=`; say
so in the docs. A malformed map (no `=` anywhere) falls back to the unformatted text like every
other bad format, never throws.

This needs no component change: `color`, `text` and an image `path` are all already bindable
properties, so one feature makes all three react.

Tests: each rule above, plus a map on a number, a text and a time; an empty map; a map with no
match and no `*`; a `*`-only map. Also one resolver-level test that a `text` component's `color`
bound to a map paints the mapped colour (find the nearest existing colour-resolution test and
follow it).

### A3. A number that arrives as JSON text can still be formatted

Evidence: Coinbase returns `"amount": "64394.01"` as a **string**, so
`btc.json.data.amount | "{0:N0}"` printed `64394.01` and drew `64632.235` on the wallpaper.

In `Value.ToText`, when the format is composite (contains `{0`) **and** carries a specifier
(contains `{0:`) **and** the value is a `TextValue` whose text parses as a `double` under
`InvariantCulture`, format the parsed double instead of the string.

Deliberately narrow: `{0}` with no specifier still formats the original text, so a value like
`"007"` or `"1.10"` is unchanged unless the author asked for a number format.

Tests: `"64394.01" | "{0:N0}"` gives `64,394`; `"64394.01" | "{0}"` gives `64394.01` unchanged;
`"abc" | "{0:N0}"` gives `abc`; a real `NumberValue` is unaffected.

### A5. `hardware` has values within seconds, not within a minute

Evidence: adding a hardware source in the editor showed an empty value tree for up to 60 s, which
reads as broken.

Two changes in `HardwareSource`:

1. Start the sampler timer with a due time of **zero** instead of `_sample`, so the first reading
   is taken immediately rather than 10 s later. (CPU load is a delta between two readings, so CPU
   may still need the second one; RAM and GPU are instantaneous.)
2. `NextDue` returns **now** while the source has published no averages yet, the way `FileSource`
   returns now on a changed mtime. Once any ring has data it goes back to the whole-minute
   boundary it uses today. This must not cost the daemon a second wake per minute once warm --
   that is the ruling `NextDue` exists for, and its comment says so.

Tests: with a fake reader, a new source's `NextDue` is now; after a sample it is the next minute
boundary; `RefreshAsync` on a fresh source with `autoStart: false` plus one `SampleOnce` publishes
`ram`. Check `tests/.../HardwareSourceTests.cs` for the existing fake.

### A6 (Core half). `runtime:` works in a command's and a file's paths

Evidence: the Downloads and Progress widgets carry
`C:\Users\JosephPitts\AppData\Local\DeskWall\scripts` inside them, so the template is not portable.

`CommandSource` already expands `%ENV%` in `command` and `workingDir`, and `FileSource` in `path`.
Teach the same three settings the `runtime:` prefix that image paths already understand
(`LayoutResolver` line ~116): `runtime:scripts\progress.ps1` ->
`%LOCALAPPDATA%\DeskWall\scripts\progress.ps1`, honouring `DESKWALL_HOME`. Put the expansion
somewhere both Core sources and the resolver can call rather than copying it; keep the resolver's
behaviour identical.

Tests: expansion under a `DESKWALL_HOME` the test sets; a path with no prefix is untouched; a
`%ENV%` path still expands.

## Lane B (Designer): item 4, and the editor half of 6

Branch `lane/drive-knob`. Files: `src/DeskWall.Designer/Model/Widgets/WidgetInstance.cs`,
`Adjustable.cs`, `WidgetDocument.cs`, `Views/PropertiesPanel.xaml.cs`,
`Views/WidgetEditorWindow.xaml(.cs)`, `Model/Widgets/SourceForms.cs`, `docs/layout-format.md`
(the Widgets and `sets` sections only). Tests in `tests/DeskWall.Designer.Tests/Widgets/`.

### B4a. A `:{token}` knob can write a **binding**, not only a literal

`WidgetInstance.ApplyTokenGroup` reads the template's value with
`templateProp.Get(templateComponent)?.LiteralText ?? ""` and writes
`PropertyValue.Literal(...)`. A **bound** property therefore substitutes into `""` and is
overwritten with an empty literal. Any hand-written template with a token knob on a binding is
silently broken today.

Fix: when the template's property is bound, substitute into the binding's **text**
(`Binding.ToString()` round-trips, check it) and write back `PropertyValue.Bound(Binding.Parse(...))`.
Literal targets keep today's behaviour exactly.

Test first: a two-component widget whose bindings hold `disks.drives[{drive}].usedFraction` and
`...freeGB`, a knob with two `:{drive}` sets; setting it to `D` gives both components bindings
naming `[D]`; setting it again to `E` still works (the template, not the instance, is the source
of the substitution -- the existing comment explains why).

### B4b. The editor can make a Drive knob

Evidence: a Drive C widget hard-codes `C` in two bindings, and pointing it at D: means editing
both by hand. `KnobType.Drive` exists and the knobs panel already fills it from the machine's
fixed drives; the editor simply cannot produce one.

In the editor, when a **bound** property's binding indexes `disks.drives[<letter>]`, the Knob
toggle on that row makes a **Drive** knob instead of refusing (it refuses every bound property
today). Making one:

- rewrites that binding's key segment to `{drive}` **in the saved template only** (the document on
  screen keeps the real letter so the canvas still draws real data);
- emits `sets` entry `components.<id>.<prop>:{drive}`;
- if a Drive knob already exists on this document, **adds the target to it** rather than making a
  second knob, so one picker drives the dial and the caption together. This is the first knob with
  more than one target; the Adjustable card lists them.

Keep it to `disks.drives`. A general "make this part of the value adjustable" is a bigger feature
and is not this task.

Tests: detection of a drive-keyed binding (and non-detection of `disks.drives` without a key, or a
key that is an index); one knob gathering two targets; the saved template's bindings carrying
`{drive}` while the document's keep `C`; round trip through `WidgetTemplate.Load`; and an
end-to-end `WidgetInstance.Add` + `SetKnob("D")` over the saved template proving both components
follow. The five-knob save limit still applies.

### B6 (editor half). Don't bake an absolute path into a command widget

The command form's **Working folder** must not default to an absolute path. Default it to
`runtime:scripts` (lane A makes that work), and say in the field's tooltip that `runtime:` means
the DeskWall folder and `%USERPROFILE%` and friends also expand. Same for the file form's
**File** field placeholder: prefer `runtime:` over `C:\Users\<name>\...`.

Depends on lane A's A6. Write it anyway: if A6 has not merged when you finish, say so in your
report and leave the tooltip wording as agreed.

## After both lanes (controller)

Rebuild the six dogfood widgets against the fixes to prove them on screen: Progress with no script
at all (A1), Volume with a colour that changes on mute (A2), Bitcoin rounded (A3), Drive with a
working Drive knob (B4), and a hardware widget showing numbers immediately (A5). Then publish and
hand to Joe.
