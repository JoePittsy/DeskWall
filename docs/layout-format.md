# Layout file format

A layout is one JSON file. It names a base image, the sources it needs, and the components
placed on the canvas in physical pixels. Source of truth for this document: `LayoutFile.cs`,
`ComponentDef.cs`, `PropertyValue.cs`, `WidgetCopy.cs`, `Bindings/*.cs`, `Widgets/*.cs` and
`Resolve/LayoutResolver.cs` in `src/DeskWall.Core`; for the designer, `Model/Lens.cs`,
`Model/Copies.cs` and `Model/Widgets/` in `src/DeskWall.Designer`. Every default below is the one in the code, not a recommendation.

A file DeskWall writes (the designer's Save, `deskwall migrate`) leaves out every property that
still holds its default, except `version` and the required ones, and escapes only what JSON
requires: `"{0:N0}°"` stays readable, not `"{0:N0}\u00B0"`. A property left out
therefore follows its default, so changing a default in the code reaches every file that never set
it, exactly as a knob at its default does for a copy.

## Top-level file

| Property | Type | Default | Notes |
|---|---|---|---|
| `version` | int | `1` | `1` or `2`; this build reads both. A file with a higher version is rejected, not half-read. Version 2 uses linked copies ("Copies"); version 1 uses stamped instances, read only to migrate them ("Version 1: stamped instances"). |
| `baseImage` | string or `{"bind": ...}` | required | Path to a JPEG or PNG, or a binding that resolves to one each tick. `runtime:` and `%ENV%` expand. See "The base image" below. |
| `baseFit` | `"cover"` \| `"contain"` \| `"stretch"` | `"cover"` | How the base image fills the canvas. |
| `encode` | `"jpeg"` \| `"png"` | `"jpeg"` | Output format for the composed frame. |
| `jpegQuality` | int | `92` | Passed to the WIC JPEG encoder, clamped to 1..100. Ignored when `encode` is `"png"`. |
| `sources` | array of source declarations | `[]` | See `docs/sources.md`. |
| `components` | array of components | `[]` | Drawn in `z` order (ties keep file order). |
| `copies` | array of copies | absent | Version 2: linked widget copies, expanded into components at load. See "Copies". Omitted from the file when absent. |
| `widgets` | object | absent | Version 1 only: stamped-instance bookkeeping, read only by the migrator. See "Version 1: stamped instances". Omitted from the file when absent. |

Example:

```json
{
  "version": 1,
  "baseImage": "C:\\Windows\\SystemApps\\...\\image_3.jpg",
  "baseFit": "cover",
  "encode": "jpeg",
  "jpegQuality": 92,
  "sources": [ { "name": "time", "type": "time" } ],
  "components": [ { "type": "text", "id": "clock", "rect": [3220, 40, 172, 78], "z": 1,
    "text": { "bind": "time.now | HH:mm" }, "font": "Segoe UI Light", "size": 64, "weight": 300, "align": "right" } ]
}
```

### The base image

`baseImage` is a `PropertyValue` like a component property ("Properties as literal or binding"):
a literal path, or `{"bind": ...}` resolved against the same value tree as the components on
every tick. Either way the result goes through `Paths.ExpandPath`, so `runtime:assets/...`
resolves under the runtime dir and `%VAR%` expands. A binding needs its source declared in
`sources` like any other. The usual one picks a photo per phase of the day with a map
(`layouts/alpine-vision-photos.json`):

```json
"baseImage": { "bind": "time.phase | \"?night=runtime:assets/alpine/ridge-night.jpg,dawn=runtime:assets/alpine/ridge-dawn.jpg,day=runtime:assets/alpine/ridge-day.jpg,dusk=runtime:assets/alpine/ridge-dusk.jpg\"" }
```

- **The swap tick.** The base cache key is taken from the *resolved* file (path, mtime, canvas
  size, fit), so the tick the phase changes on sees a different base key, skips the incremental
  path and renders every component onto the new photo. Every other tick is exactly as before.
- **Four photos stay decoded.** `BaseCache` keeps the four most recently used bases per canvas size
  as pre-scaled raws (`runtime/base/<w>x<h>-<key>.raw`), so after the first day each swap reads a
  raw instead of decoding a JPEG. The fifth photo evicts the one used longest ago. The LRU is per
  canvas size because the designer renders its preview into the same `base\` at its own size, and
  one global LRU would let the preview evict the daemon's photos. Measured at 3440x1440 (Debug
  JIT, JOES-XPS-17, 2026-09-30): a first-seen photo costs a 48-149 ms decode, a cached one about
  0 ms, and a warm swap tick draws in the same 80-125 ms as the minute ticks on either side of it.
- **A missing file keeps the last good base.** When the resolved path does not exist, the tick
  draws on the base it used last and logs one warning (`base image X not found; keeping Y`), not
  one a minute; the warning is re-armed once the path resolves to a file again. With no previous
  base on disk (the first tick on a fresh runtime dir, before the photos are copied in) it draws
  on the first of the map's other photos that exists (`base image X not found; no previous base;
  using Y`), and failing that on a solid black base (`...; using a solid base`), so the first tick
  still produces a wallpaper. When the photo turns up its base key moves and the next tick
  renders onto it. All of this applies to a literal path too, which has no other photos and goes
  straight to the solid base.
- **A photo per phase replaces the sky-grade overlays.** A layout that tints the sky with
  full-canvas layers bound to `time.dayFraction` (the `sky-grade`, `sky-top` and `sky-band`
  layers of `alpine-vision.json`) is painting the mood on top of a photo that cannot change. When
  the photo itself is graded per phase, those layers are not needed:
  `alpine-vision-photos.json` keeps them at half alpha, as a light wash that still moves minute
  by minute between the four photos' hard cuts, and a new layout can leave them out.
  `scripts/phase-photos.ps1` makes the graded photos from one source photo.
- **In the designer** the Layout panel's photo row has the same Bind chip and Unbind menu as any
  other property, and the preview resolves the photo against the live (or pinned) values.
- An empty resolve (a map with no match and no `*`) is a missing file, not the empty canvas;
  give the map a `*=` entry if a value outside the list is possible.

## Every component: shared properties

`ComponentDef` is the base every component type inherits:

| Property | Type | Default | Notes |
|---|---|---|---|
| `id` | string | required | Unique across the resolved layout (including repeater-expanded children: `<repeaterId>[<index>].<childId>`). A duplicate throws before anything draws. |
| `rect` | `[x, y, w, h]` | required | Physical pixels, absolute in the layout's own coordinate space. Encoded as a 4-element JSON array, not an object (`RectConverter`). |
| `type` | string | required | Discriminator: `text`, `image`, `bar`, `dial`, `line`, `shortcut`, `repeater`. An unknown type fails the parse. |
| `z` | int | `0` | Draw order, ascending. |

## Component types

### `text`

| Property | Default | Notes |
|---|---|---|
| `text` | required | The string drawn. |
| `font` | `"Segoe UI"` | Family name passed to DirectWrite. Missing fonts fall back to Segoe UI (spec 6). |
| `size` | `16` | Points. |
| `weight` | `400` | DirectWrite font weight. |
| `color` | `"#EBFFFFFF"` | ARGB hex. |
| `align` | `"left"` | `left` \| `center` \| `right`. |
| `effect` | `"shadow"` | Text effect style. |
| `effectRadius` | `"auto"` | Blur radius in pixels for the shadow. `"auto"` (and any other non-number) means a tenth of `size`, floored at 1 px: 64 -> 6, 40 -> 4, 13 -> 1. A flat 6 px is a drop shadow on a 64 px clock and a dark crust on a 13 px label, so the default follows the font; set a number to pin it. |
| `effectColor` | `"#A0000000"` | ARGB hex for the shadow/outline/plate. |

### `image`

| Property | Default | Notes |
|---|---|---|
| `source` | required | Local path or `http(s)` URL. A remote URL is resolved through the remote image cache before drawing (`docs/sources.md`); a cache miss draws the same fallback plate as a missing local file. A local path may start with `runtime:` to be resolved under the runtime dir (`runtime:assets/weather/{0}.png`). |
| `fit` | `"cover"` | `cover` \| `contain` \| `stretch`. |
| `radius` | `0` | Corner radius in pixels. |
| `opacity` | `1` | 0..1. |
| `tint` | `""` | ARGB colour every pixel is multiplied by (RGB by RGB, alpha by A); empty is none. A white PNG tinted by a blend on `time.dayFraction` is one asset that is red at dawn and gold at noon. Part of the content key. |

The content key of an image includes the local file's last-write time, so a file replaced in
place under the same path redraws.

### `bar`

| Property | Default | Notes |
|---|---|---|
| `fraction` | required | 0..1. A bound fraction that does not resolve hides the bar (track, fill and glow): unknown is not 0. A resolved 0 draws the track as usual. |
| `track` | `"#46FFFFFF"` | Background fill. |
| `fill` | `"#EBFFFFFF"` | Fill below `threshold`. |
| `threshold` | `1` | At or above this fraction, `thresholdFill` is used instead of `fill`. |
| `thresholdFill` | `"#D13438"` | |
| `direction` | `"horizontal"` | `horizontal` \| `vertical`. |
| `shape` | `""` | SVG path data (`M L H V C Z`, absolute and relative) drawn instead of the box. Its bounds are stretched to `rect`, so any path works and resizing the bar resizes it; an axis the path does not span (a flat line, a dot) sits on the rect's centre line. The track is the whole path, the fill the same path clipped to the fraction. Unreadable data draws the plain box. |
| `thickness` | `0` | With a `shape`: 0 fills it, more strokes it this wide (round caps and joins), inset by half so the ink stays inside `rect`. |
| `glow` | `0` | With a `shape`: a soft halo this many px round the lit part, in the fill colour (stacked low-alpha strokes, like a text shadow). The path is inset by it too, so inflate `rect` by the glow to keep the line where it was. |
| `glowColor` | `""` | With a `glow`: the halo's ARGB colour; blank is the fill colour. |
| `glowStrength` | `0.12` | Each of the four glow strokes' share of the glow colour's alpha, 0..1. 0.12 is a halo you have to look for; 0.3 to 0.45 is one you cannot miss. |
| `opacity` | `1` | 0..1, multiplied into track, fill and glow colour. A bound opacity that does not resolve hides the bar. Lets the weather veil stars whose colour is already bound to the time of day. |

A shaped bar traced from the base photo's skyline, lit left to right by the volume, is a ridge line
over the mountains (the `rect` is the path's bounds inflated by half the stroke, so it lands on the
photo exactly):

    { "type": "bar", "fraction": { "bind": "audio.volume" }, "shape": "M0,90 L22,80 ...",
      "thickness": "3", "track": "#55FFFFFF", "threshold": "2", "rect": [-2, 355, 3443, 467] }

Threshold colouring is a component property, not something a binding expression computes; the
spec deliberately keeps bindings free of conditionals (spec 4.2).

### `dial`

A thin arc. Same value semantics as `bar` (clamped fraction, threshold colouring), drawn as a
stroked arc centred in `rect` with radius `min(w, h) / 2 - thickness / 2` and flat caps.

| Property | Default | Notes |
|---|---|---|
| `fraction` | required | 0..1, clamped. A bound fraction that does not resolve hides the dial, as for `bar`. |
| `track` | `"#46FFFFFF"` | Full-sweep arc under the fill. |
| `fill` | `"#EBFFFFFF"` | The value arc, below `threshold`. |
| `threshold` | `1` | At or above this fraction, `thresholdFill` is used instead of `fill`. A fraction of 1 is therefore at the default threshold, exactly as for `bar`. |
| `thresholdFill` | `"#D13438"` | |
| `thickness` | `6` | Stroke width in pixels. Scales with the smaller of the two display factors, not the geometric mean, because the radius comes from the short side of the rect. |
| `startAngle` | `225` | Degrees **clockwise from 12 o'clock** where the sweep begins. |
| `sweep` | `270` | Degrees of arc for fraction 1. 360 or more draws a closed ring (as two arcs; one D2D arc segment cannot describe a full turn). |
| `opacity` | `1` | 0..1, applied to track and fill. A bound opacity that does not resolve hides the dial (the battery dial fades out on mains at full charge this way). |

Angles are always clockwise from 12 o'clock, so the default `225` / `270` is the familiar gauge
open at the bottom. Nothing but the arc is drawn: a number inside the dial is an ordinary `text`
component the layout places over it.

### `shortcut`

Draws nothing itself; it tells the shortcut manager where to place a desktop icon (spec 7).

| Property | Default | Notes |
|---|---|---|
| `target` | required | A program plus arguments, or a URI (e.g. `steam://rungameid/620`). An empty or whitespace-only resolved target means no icon is placed for this instance (`LayoutResolver.Emit`). |
| `tooltip` | `""` | |
| `slot` | `0` | The desktop icon slot this shortcut owns. Inside a repeater this is the **base**; child `n` gets `slot + n`. Two shortcuts (standalone or repeater-expanded) resolving to the same slot is a layout error and throws (`ShortcutPlan.Ordered`). |

### `repeater`

Expands into concrete child components at resolve time; nothing named `repeater` exists in the
final drawn output.

| Property | Default | Notes |
|---|---|---|
| `items` | required, must be a binding | Resolves to a list. A non-binding `items` or a binding that does not resolve to a list throws / emits nothing. |
| `axis` | `"vertical"` | `vertical` \| `horizontal`: the direction items stack in. |
| `gap` | `0` | Pixels between item cells along the axis. |
| `cellHeight` | `"auto"` | A literal number of pixels, or the literal string `"auto"` (case-insensitive) to size the cell from the first image child's aspect ratio. Despite the name, it applies to whichever axis the repeater runs on (width, for a horizontal repeater). |
| `template` | required | A list of child `ComponentDef`s, positioned relative to the item's cell origin (their `rect` is an offset, not an absolute position). |

## Properties as literal or binding

Every component property except `id`, `rect`, `type`, `z`, `axis`, `gap`, `slot` and a repeater's
`template` is a
`PropertyValue`: a JSON string/number/boolean literal, or an object `{"bind": "<binding text>"}`.
There is no third form; a property is either fixed at authoring time or fully driven by a source.

```json
"size": 64                                  // literal number
"text": { "bind": "time.now | HH:mm" }      // bound
```

## Binding grammar

A binding is a path into a source's published value tree, optionally followed by `| "<format>"`
(the quotes are optional if the format has no spaces or pipes). Grammar (`BindingParser.cs`):

    path       := name (('.' name) | '[' index ']' | '[' slice ']' | '[' key ']')*
    name       := (letter | '_') (letter | digit | '_' | '-')*
    index      := non-negative integer literal
    slice      := [index] '..' [index]     at least one side; start <= end
    key        := any text without ']'; looked up by the list's key field, case-insensitively as text

Thirteen examples, each valid against the value trees the built-in sources publish:

1. `time.now` -- the raw `TimeValue`, no format (falls back to `"o"` round-trip formatting).
2. `time.now | HH:mm` -- a plain .NET format string applied to the resolved value's own type.
3. `disks.drives[C].free | "{0:N0} GB free"` -- key lookup (`C` matched against the `letter`
   field) then a composite format (`{0` present) applied to the raw CLR value.
4. `disks.drives[0].letter` -- index lookup into the same list.
5. `disks.drives[C].usedFraction` -- unformatted number, typically bound straight to a `bar`'s `fraction`.
6. `system.daysSinceCrash` -- a plain number; `-1` when no crash event has been found.
7. `system.pendingReboot` -- a `BoolValue`; formats as `"True"`/`"False"` with no format string.
8. `steam.json.response.games[0].appid | "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg"` --
   composite format building a URL from a JSON field three levels deep.
9. `rssFeed.items[0].title` -- index into a list published by an `rss` (or `file`-parsed-as-rss) source.
10. `command1.json.some-key` -- a hyphenated field name from a `command` source's parsed JSON; `-` is legal inside a name after the first character.
11. `weather.json.current.temperature_2m | "{0:N0}°"` -- composite format on a field published by
    an `http` source (`layouts/column-system.json`'s Open-Meteo recipe).
12. `weather.json.current.weather_code | "runtime:assets/weather/{0}.png"` -- composite format
    building a path instead of a URL; the leading `runtime:` is a token `LayoutResolver` expands
    against the runtime directory (not the repo) after formatting, so a committed layout never
    names a per-user absolute path. The token is a prefix rather than `{runtime}` because a
    composite format would swallow the braces. The `weather` widget draws the night icon as a
    second part bound to `"runtime:assets/weather/{0}-night.png"`, and picks between the two with
    an opacity Step on `weather.json.current.is_day` (`"?0=0,*=1"` and `"?0=1,*=0"`): a binding
    reads one path, so a suffix chosen by a second value is two parts, not one format. See
    `assets/weather/README.md` for the icon set this recipe
    expects at that path.
13. `hearth.json.games[..4]` -- a **slice**: the first four items of the list, still a list (so it
    is what a repeater's `items` binds), keeping the list's key field so `[..4][<id>]` and
    `[..4][0]` work on the slice. See "Slices" below.

Three format rules matter (`Value.ToText`, spec 4.2):

- A format string whose first character is `?` is a **map** (below).
- A format string containing `{0` is treated as a *composite* format and applied with
  `string.Format` to the value's raw CLR object (the underlying string, double, `DateTimeOffset`
  or bool). Anything else is applied as a plain .NET format string to the value's own
  `ToString`/`ToString(format)` (so `"N0"` works on a number, `"HH:mm"` on a time).
- A malformed format (an argument index the value does not supply, an unbalanced brace, an
  unknown type specifier) falls back to the unformatted text rather than throwing. A layout
  authoring mistake must never abort a tick.

### Slices: the first N items of a list

`[start..end]` takes the items from `start` (inclusive, default 0) to `end` (exclusive, default the
end of the list), counted as a C# range counts: `[..4]` is the first four, `[2..]` everything after
the first two, `[1..3]` the second and third. Bounds past the end clamp rather than fail, so
`[..8]` of a three-item list is those three and `[5..]` of it is an empty list (a repeater over it
draws nothing). Slicing anything that is not a list resolves to `null`, like any other wrong-shaped
step. `[..]`, a reversed `[3..1]` and a negative bound are parse errors or (negative) a key, as
`[-1]` already is.

A slice is how a "how many" knob trims a list. `layouts/widgets/recent-games.json` binds its
repeater to `hearth.json.games[..{count}]` and its Count knob substitutes the number with the
`:{token}` form ("Knobs and the `sets` grammar"):

```json
"items": { "bind": "hearth.json.games[..{count}]" },
...
{ "id": "count", "type": "choice", "default": "4||4||504",
  "choices": [ "1||1||120", "2||2||248", "3||3||376", "4||4||504", ... ],
  "sets": [ "components.games.items:{count}", "components.games.w" ] }
```

The count no longer depends on the repeater's width: widening the part (an override, or a drag in
the designer) leaves the same four covers and the same shortcut slots. The knob still sets the
width too, because that is the widget's footprint. Until slices existed the knob set *only* the
width and let "Overflow stops" drop the rest. A template's `[..{count}]` parses as a key until
the knob substitutes a number, so an unsubstituted placeholder resolves to nothing rather than
failing the layout.

`..` and not Python's `:` because a key is free text and `12:30` is a plausible one; neither
`..` nor a bare number on each side of it is a plausible key.

### A number that arrived as JSON text

A JSON API is free to return a number as a string (Coinbase's `"amount": "64394.01"`). When a
composite format carries a **specifier** -- `{0:` something, as in `"{0:N0}"` -- the author has
asked for a number, so a text value that parses as a `double` under the invariant culture is
formatted as that number:

    btc.json.data.amount | "{0:N0}"      "64394.01" -> 64,394

This is deliberately narrow. A bare `{0}` with no specifier formats the original text unchanged,
so `"007"` stays `007` and `"1.10"` stays `1.10`. A text that does not parse (`"abc"`) is left
alone, and the parse rejects thousands separators, so `"1234,6"` is never read as `12346`.

### The map format: a value picks a string

A format beginning with `?` is a comma-separated list of `key=text` pairs. The key is matched
against the value's **own plain text** (what it renders with no format at all),
case-insensitively, and the matching entry's text is the result.

    volume.json.muted | "?true=#D13438,false=#EBFFFFFF"     a bool driving a colour
    volume.json.muted | "?true=muted"                       text when true, nothing when false
    weather.json.current.is_day | "?1=day,0=night,*=?"      a number driving text

- `*=text` is the fallback for any value no key matched.
- A key may be a **comparison** on the value as a number: `<0.3`, `<=0.3`, `>0.7`, `>=0.7`.
  Entries are tried in order and the first that holds wins, so
  `hardware.cpu | "?<0.3=#0078D4,<0.7=#107C10,*=#0078D4"` is three bands. A value that is not a
  number matches no comparison. The designer's bind menu writes these as "Step" rules.
- **No match and no `*` is the empty string**, not the unformatted value: drawing nothing when a
  flag is false is the point of the `muted` case above.
- Keys are trimmed, so `?a=one, b=two` works; the picked text is taken verbatim, so it may be
  blank or carry spaces.
- A key or a picked text cannot contain `,` or `=`; there is no escape.
- A format starting with `?` with no `=` anywhere is **not** a map and is handled as an ordinary
  format string, so nothing that worked before maps existed changed.

### The blend format: a value slides between stops

A format beginning with `~` is a list of `at=value` stops: the value, as a number, is placed
between the two stops either side of it and the result interpolated. Numbers interpolate as
numbers, colours per ARGB channel, anything else steps at the midpoint; below the first stop or
above the last, that stop. Stops may be in any order. A value that is not a number is the empty
string. The bind menu writes these as "Blend" rules.

    hardware.cpu | "~0=12,1=48"                  font size 12 at 0%, 48 at 100%
    hardware.gpuTempFraction | "~0.5=#107C10,0.9=#D13438"   green shading to red

A number property (size, weight, opacity, a dial's sweep) bound with a map or a blend takes the
result as its number; any other format on a number property is still ignored.

No component knows about maps. `color`, `text` and an image's `source` are all ordinary bindable
properties, so the one rule in `Value.ToText` makes all three react to a bool.

A binding that cannot be resolved (a missing field, an out-of-range index, a key with no match,
indexing into the wrong shape of value) resolves to `null`; the bound property then falls back to
its own default (spec 3.2: staleness and missing-value handling are the component's problem, not
the binding's). The exceptions are the gauges: a `bar` or `dial` whose bound `fraction` does not
resolve is hidden rather than drawn at 0, because an empty track reads as a confident zero. A numeric property treats `NaN` and `Infinity` the same way, whether written
literally, bound to a text that parses as one, or bound to a source value that is one: the
property takes its default rather than handing a non-finite width or alpha to Direct2D.

## Repeater semantics

The repeater is the only construct that turns one component definition into several. It runs
once per tick, after sources are refreshed and before content keys are computed
(`LayoutResolver.Resolve` / `Emit`).

- **Item scope.** Each item in the bound list becomes the scope every template child's own
  bindings resolve against (so a template binds `appid`, not `steam.json.response.games[0].appid`).
- **Cell extent ("auto" and clamping).** For each item, the cell's extent along the repeater's
  axis is `max(declared, furthest template child edge)`:
  - *declared* is `cellHeight`'s literal number, or, when `cellHeight` is `"auto"`, the extent
    computed from the **first image child's** aspect ratio applied to that child's own template
    width (vertical axis) or height (horizontal axis). A remote image source is resolved through
    the image cache's `Lookup` (never a blocking fetch); a cache miss, or no local file yet, uses
    the same 2:3 placeholder ratio the POC used.
  - *furthest template child edge* is the largest `Bottom` (vertical axis) or `Right`
    (horizontal axis) across every child in the template, so a child positioned below or beside
    the image never spills into the next item's cell even if it exceeds the declared/auto extent.
  - Every child rect is then **clamped** to stay inside its own cell on the main axis and inside
    the repeater's own rect on the cross axis (`ClampToCell`). Children are clamped, never
    dropped, so a too-wide child paints a narrower box instead of painting over whatever is
    beside the repeater block.
- **Overflow stops, it does not wrap or shrink.** Items are laid out one after another
  (`cursor += cell + gap`); the loop stops (`break`) at the first item that would not fit inside
  the repeater's own rect. There is no scrolling and no proportional shrink-to-fit.
- **Slot offset.** A `shortcut` template child's `slot` is `base + index`, `index` being the
  item's position in the resolved list (0-based), not the visual row after any items were
  skipped for overflow.
- **Ids.** An expanded child's id is `<repeaterId>[<index>].<childId>`, which is what makes a
  duplicate id across two repeaters (or a repeater and a standalone component) detectable at
  resolve time.
- **Image pixel sizes are cached** by path and file mtime (`LayoutResolver.PixelSize`), so a
  repeater whose "auto" cell height depends on cover art does not decode every cover on every
  tick that changes nothing else; the cache is cleared wholesale past 256 entries.

## Display signatures and scaling

A display signature is `<monitor device path> @ <width>x<height> @ <scale>%`
(`DisplaySignature.Key`). The layout store maps signatures to layout files
(`%LOCALAPPDATA%\DeskWall\layouts.json`); `deskwall layouts set <path>` registers one for the
current display.

### Switching layouts

`deskwall layouts use <name|path>` (alias `deskwall theme`, #31) and the designer's layout picker
(the layout name in its top bar, #73) are one switch, `LayoutLibrary.Use`:

- **The library** is every `*.json` in `%LOCALAPPDATA%\DeskWall\layouts\` except `migrate`'s
  `*.v1.json` backups, plus any file `layouts.json` already names from elsewhere. A name is the
  file name, with or without `.json`.
- **What moves:** the file this display draws from (its own entry, or the closest match's when it
  has none) is replaced by the new one in *every* entry that names it, in one write of
  `layouts.json`. Displays that share a layout (the console panel and Apollo's virtual display on
  JOES-PC) keep sharing it; a display with a layout of its own is untouched; a display that
  resolves by closest match still does, so it stays scaled. With an empty store, the entry is the
  current display's. The chosen file's write time is set to now, because closest-match ties go to
  the most recently written file and a library file (or an imported copy, which keeps its
  source's time) can be months old. The store is re-read first, so a long-lived one (the
  designer's) never writes back entries another process changed.
- **A file from outside the library is copied in** under its own name first, so the repo's
  `layouts\` stay templates. The same bytes already there are reused; a different file of that
  name is never overwritten, and the switch is refused.
- The file must parse and be a version this build reads, or nothing is written. Assets and
  scripts it references (`runtime:assets/...`, `runtime:scripts/...`) are not installed by the
  switch (#90).

The daemon needs nothing new: its layout watcher already watches `layouts.json` and re-resolves
on a write. Measured with the real `LayoutWatcher` on a scratch home: no callbacks while idle, one
callback 425 ms after the switch (300 ms of it is the watcher's debounce).

When the current display has no exact entry, the store picks the closest existing layout
(`DisplaySignature.Similarity`: +3 for the same device path, +1 for aspect ratio within 1
percent, +1 for the same resolution; ties broken by the most recently written file) and scales
it (`LayoutScaler.Scale`) rather than leaving the canvas blank:

- Every component `rect` scales by `(sx, sy) = (toWidth / fromWidth, toHeight / fromHeight)`.
- `TextDef.Size`, `TextDef.EffectRadius` and `ImageDef.Radius` scale by the geometric mean
  `sqrt(sx * sy)`, so a font or corner radius does not stretch non-uniformly. An
  `effectRadius` of `"auto"` is left alone, like `cellHeight`: it is derived from the scaled
  `size` when the layout is resolved, so scaling it here would apply the factor twice.
- `DialDef.Thickness` scales by `min(sx, sy)` instead: the arc's radius is taken from the short
  side of its rect, and the geometric mean is 1 for a display that halves in width and doubles in
  height, which would leave the stroke wider than the ring it is drawn on.
- A line's and a shape bar's `thickness` and `glow` scale by `min(sx, sy)` too, kept to two
  decimals: the path is inset from its rect by `thickness / 2 + glow` on both axes, so a halo
  authored to just meet the rect's edges still does after the rect shrinks. An axis a shape does
  not span (a flat stroke, a dot) is drawn on the rect's centre line, so it stays centred when
  the two factors differ.
- A repeater's `gap` scales by the same geometric mean; its `cellHeight` scales by `sy` (vertical
  axis) or `sx` (horizontal axis) unless it is `"auto"`, which is resolution-independent by
  construction and is left alone.
- Scaling is a deep copy (`LayoutFile.Parse(source.ToJson())`); the registered layout file on
  disk is never rewritten by a scale. The daemon logs the substitution and the designer offers
  to save the scaled result as the new signature's own layout (spec 5).

A layout registered for the exact current signature is used as-is (`Scaled: false`); a layout
that fails to parse or whose `version` exceeds `LayoutStore.MaxVersion` is reported through the
log/tray and treated as if nothing were registered for that signature (the previous wallpaper
stays, spec 3.2) -- it is never used as a stand-in for another display's request.

## Copies

Version 2 of the format links a placed widget to its widget file instead of stamping a copy of it
into the layout. A layout keeps `components` for things placed by hand and gains `copies`: each
entry names a widget by key and stores only where it sits and what the owner changed. The expander
(`WidgetExpander.Expand`, `src/DeskWall.Core/Widgets/`) turns the copies into ordinary sources and
components at load, before scaling and resolve, for the daemon and the designer alike, so what the
designer shows is what gets painted. Resolve and render never see a copy, and the tick does not
pay for expansion. The model is `WidgetCopy` (`src/DeskWall.Core/Layout/WidgetCopy.cs`).

```json
{
  "version": 2,
  "baseImage": "...",
  "sources": [ ],
  "components": [ ],
  "copies": [
    { "id": "dial-2", "widget": "dial", "x": 3312, "y": 400, "z": 0,
      "knobs": { "metric": "GPU||hardware.gpu||hardware.gpuPct | \"{0}%\"||gpu" },
      "overrides": {
        "components.value.color": "#FFFFC000",
        "components.label.rect": "0,60,80,18",
        "components.label.hidden": true,
        "components.drives.letter.size": 15,
        "sources.hardware.every": 10
      } }
  ]
}
```

| Property | Type | Default | Notes |
|---|---|---|---|
| `id` | string | required | Unique among the layout's copies, `"<widgetKey>-<n>"`. |
| `widget` | string | required | The widget's key: its file name without `.json`. A key never changes after the widget is created, so a copy never needs re-pointing. |
| `x`, `y` | int | `0` | The copy's origin on the canvas. Every part's `rect` is offset by it. |
| `z` | int | `0` | Added to every part's own `z`. |
| `knobs` | object | `{}` | Knob id to the value the owner set. A knob left at its default is not stored, so changing a widget's default reaches every copy that never touched it. Values use the knob value conventions in "Knobs and the `sets` grammar". |
| `overrides` | object | `{}` | Override key to value; see below. |

**Override keys** reuse the knob `sets` grammar:

- `components.<partId>.<property>`: a property of one of the widget's parts.
- `components.<repeaterId>.<childId>.<property>`: a property of a repeater's template child. Part
  ids are `[A-Za-z_][A-Za-z0-9_-]*`, so they never contain dots and four segments are unambiguous.
- `sources.<name>.settings.<key>` and `sources.<name>.every`: one of the widget's sources, by its
  name in the widget file.
- Three pseudo-properties of a part: `rect`, a literal `"x,y,w,h"` relative to the copy's origin;
  `z`; and `hidden`, a literal `true`, which leaves the part out altogether. That is how a copy
  deletes a part.

**Override values** are ordinary property values: a literal (string, number or boolean) or
`{ "bind": "..." }`, exactly as in a component.

**Precedence:** the widget file, then the knob values (the copy's own, else the knob's default),
then the overrides. Resetting an override deletes its key, and setting a value equal to what the
widget and knobs already give also deletes it, so the file only ever holds real differences.

**Orphans.** An override or knob naming a part, property or knob the widget no longer has stays in
the file untouched. The expander skips it and reports it (`OrphanOverride`, `OrphanKnob`, the
latter also for a widget knob whose `sets` entries name nothing); the designer lists it under the
copy in Layers with Remove (or Delete on the row); the daemon does not log it, because it is not an
error on the wallpaper. If the part comes back under the same id (a restored widget file), the
override applies again. Nothing is deleted silently.

The one exception is an orphan the designer itself would create. An edit to a widget in the
designer (a part deleted at widget depth, a source removed) takes what it kills with it, in the
same undo entry: each knob `sets` entry whose target went (a composite knob loses the matching
`||` part from its default, its choices and every copy's value, so the entries left keep theirs),
a knob left with no entries, and the copies' values and overrides that applied before the edit
and name nothing after it. Removing a source takes the knobs that set its settings, and the
copies' values for them, the same way. The status bar says what went; Ctrl+Z brings it all back.
A knob the owner takes off on purpose (its targets are all still there) also takes every copy's
value for it, without a notice. Remove on a widget knob orphan does the same as a widget edit for
that knob: its dead entries come out of the widget, for every copy. When every target is there and
what fails is a value that will not parse as a binding, Remove deletes the copy's own value, or,
when the copy has none and so the widget's own default is at fault, takes the knob off the widget.

**z.** Each part's z is `copy.z + part.z`, so the default of `0` keeps every part's z as the widget
authored it.

**Expanded ids** are `"<copyId>.<partId>"`, the same as a version-1 stamped instance, with the
part's `widget` field set to the copy id. Keys in `frame-state.json` carry straight across a
migration.

**Sources.** The layout's own `sources` come first. Then each copy's sources, with its knobs and
overrides applied:

- Same name and an identical definition (type, `every` and settings): shared. Four dials use one
  `hardware` sampler.
- Same name, different definition: the copy's source takes the first of `<name>2`, `<name>3`, ...
  that is free or already identical, and that copy's top-level bindings are rewritten to match (a
  repeater child binds against its item, never a source by name). Knobs and overrides are applied
  before the merge, against the widget file's own source names, so a `sources.<name>` path always
  reaches the copy's own source whatever it ends up called. Two headline copies with different
  feeds therefore each get their own source, and editing one never changes the other.

**A missing or unparseable widget file.** The daemon skips that copy, paints the rest, and reports
the problem through the log and tray (`MissingWidget`, `BrokenWidget`). The designer draws a
broken-link box at the copy's `x,y` (172x40, `Copies.BrokenSize`), never a silent blank.

**What the designer writes when a copy is edited on the canvas.** At layout depth a copy is
selected, dragged, nudged and deleted as one thing:

- Moving it changes `x`/`y` only; its overrides stay exactly as they were.
- Resizing it scales its parts in place about the copy's origin and writes the result as
  overrides: each part's `rect`, plus (on a corner drag) the literal pixel sizes a corner drag
  scales (text `size`, dial `thickness`, a repeater's `gap` and `cellHeight`). The origin stays
  put, even for a drag on the top-left grip: the parts' rect overrides carry the move. A copy whose
  widget is missing has no parts to scale, so its origin follows the box.
- Deleting it removes the `copies` entry; its sources only ever existed in the expansion.
- Any edit to one of its parts at copy depth is an override on this copy, never the widget (see
  "Editing widgets in the designer").

**Where widget files come from.** By key, from `%LOCALAPPDATA%\DeskWall\widgets\` first and then
the shipped `widgets\` beside the exe (`WidgetCatalog.Finder`: the user folder wins). Editing a
shipped widget writes the user file under the same key, so every copy of it, in every layout on
the machine, follows the edit without anything being rewritten; deleting that user file returns
them to the shipped widget, overrides intact. The known ceiling: a fork shadows every later
shipped update to that key until it is deleted. The daemon watches the user-folder path of every
key its layouts reference; the shipped folder changes only on install, which restarts it.

**Widget keys.** A key made in the designer is a slug of the widget's name (lower case, `[a-z0-9]`
runs joined by `-`; `GPU °C` -> `gpu-c`, an empty name -> `widget`), numbered `-2`, `-3` until no
user or shipped widget file, unapplied widget edit or copy in the layout already uses it, so a new
key never collides with anything. Until its first Apply the key follows the name (renaming
`widget` to "CPU gauge" makes it `cpu-gauge`, and its copies' ids follow); once a file of that key
exists it is fixed, and renaming changes `name` only, because a changed key would orphan every copy.

**Why the expander is in Core.** Expanding at load, for the daemon and the designer alike, is what
makes a widget edit reach the wallpaper on the next activation, rather than only once every layout
that uses the widget has been re-saved. The expander loads widget files through a source-generated
JSON context, because a reflection fallback compiles and passes under JIT and fails only at runtime
under native AOT; an AOT publish ticking a v2 layout is the proof.

**Opening a version-1 file in the designer** migrates it in memory (`LayoutMigrator`, only when the
result is equivalent; otherwise it opens as it is); the first Apply writes `<file>.v1.json` beside
it if that is absent, then the v2 file.

## Widgets

A *widget* is a widget file, `widgets/<key>.json`: a small, reusable recipe of some sources, some
components in widget-local coordinates starting at `(0, 0)`, and up to five *knobs*. Version-2
layouts place it as linked copies ("Copies"). The model is `WidgetTemplate`
(`src/DeskWall.Core/Widgets/`), loaded by the daemon's expander and the designer alike; the
designer's editing model is `src/DeskWall.Designer/Model/Widgets/` and `Model/Lens.cs`.

### Widget file

```json
{
  "version": 1,
  "name": "Weather",
  "description": "Temperature and sky for a town, from Open-Meteo. No key needed.",
  "size": [172, 60],
  "anchor": "top",
  "requires": null,
  "sources": [ { "name": "weather", "type": "http", "every": 900, "settings": { "url": "..." } } ],
  "components": [ { "type": "text", "id": "temp", "rect": [0, 0, 108, 52], "...": "..." } ],
  "knobs": [ { "id": "town", "label": "Town", "type": "town", "default": "...", "sets": ["..."] } ]
}
```

`name`, `description` and `size` (`[width, height]`, both positive) are required; `anchor`
(`"top"` or `"bottom"`, default `"top"`, used only by the Arranger) and `requires` (a sentence
shown on the Insert card when a requirement -- an NVIDIA GPU, Tailscale, Steam secrets -- may be
missing) are optional. `version` is written as `1` and not read. The key is the file name without
`.json` (`WidgetTemplate.Key`), not a field in the file. `sources` and `components` are ordinary
`SourceDef`/`ComponentDef` JSON exactly as in a layout, except every component `rect` is relative
to the widget's own `(0, 0)`.

`WidgetTemplate.Load` rejects, naming the file and field: a missing `name` or `description`, a bad
`size` or `anchor`, a knob with no `id` or an unknown `type`, more than five knobs, and a part id
(including a repeater's template children) that is not `[A-Za-z_][A-Za-z0-9_-]*`.

Each knob has `id`, `label` (default: the id), `type` (`number`, `text`, `choice`, `color`,
`drive`, `town`), `default`, `sets`, and optionally `choices` (for `choice`) and `min`/`max`.

### Knobs and the `sets` grammar

A knob's `sets` list names the paths a value writes to, in order. Paths are in the widget file's
own terms: template-local part ids and the widget's own source names (`KnobSets`, the same
executor that applies a copy's overrides).

| Form | Effect |
|---|---|
| `components.<id>.<property>` | Writes a **literal** to the part's property, matched by name, case-insensitively (`ComponentProperties.Find`). `rect` (`"x,y,w,h"`) and `z` work too, and `hidden` with `true` leaves the part out. |
| `components.<repeaterId>.<childId>.<property>` | The same, on a repeater's template child. |
| `components.<id>.<property>=bind:<text>` | Writes a **binding**, parsed from the knob's value, not from `<text>` -- `<text>` documents the default's shape for a human reading the file and is never parsed. A value that does not parse as a binding writes nothing. |
| `sources.<name>.settings.<key>` | Writes the named source's setting, as a literal. |
| `sources.<name>.every` | Writes the named source's refresh interval, in seconds. Not a setting: `every` is `SourceDef`'s own field and a value under `settings` is ignored by every source factory. A value that is not a positive whole number leaves the interval alone. |
| any of the above, with a trailing `:{token}` | Instead of overwriting, **substitutes** the literal `{token}` inside the target's value **as the widget file authors it** (the weather URL's `{lat}`), leaving the rest of the string as it is. Every token entry aimed at the same target is applied in one pass, so `{lat}` and `{lon}` both land in the one URL. |

The `:{token}` form works on a **bound** property as well as a literal one: when the file's
property is a binding, the token is substituted into the binding's own text and the property stays
a binding, so it goes on drawing live data. That is what a `drive` knob is made of --
`components.bar.fraction:{drive}` against a file binding of `disks.drives[{drive}].usedFraction`
repoints the bar at another drive without touching anything else in the path or its format.

An entry that finds no target (no such part, property, source or token target; a binding written
to a setting) is skipped and its knob reported as an orphan (`OrphanKnob`).

A knob's value (`default` in the file, and what a copy's `knobs` stores when it differs) may be a
**plain string** or a **composite** of parts joined by `||` (two pipes, chosen because a binding's
own `|` format separator and every value a shipped widget writes -- URLs, format strings, captions
-- use a single `|` at most). Part `0` is the whole value for a plain knob and the display value
for a composite one; for the `i`-th entry in `sets` (0-based), the value written or substituted is
part `i + 1` when it exists, else part `0`. This is how one knob drives several differently-shaped
targets:

- **Town** (`weather.json`): default `"Leeds||53.8008||-1.5491"`, `sets`
  `["sources.weather.settings.url:{lat}", "sources.weather.settings.url:{lon}"]`. Part 0
  ("Leeds") is what the knobs panel shows; part 1 substitutes `{lat}`, part 2 `{lon}` -- both into
  the *same* setting string, which is why the substitution form exists instead of a plain
  overwrite. Applying a knob never makes a network call, so a `town` knob's `default` must already
  carry coordinates; the designer turns a typed-in town into a fresh `"town||lat||lon"` value with
  `Copies.ResolveTownAsync` (Open-Meteo geocoding, `count=1`) before storing it on the copy.
- **Metric** (`dial.json`): a `choice` knob whose four `choices` are themselves full composites,
  e.g. `"GPU temperature||hardware.gpuTempFraction||hardware.gpuTempC | \"{0}°\"||gpu °C"`.
  `sets` has three entries -- `components.dial.fraction=bind:...`, `components.value.text=bind:...`,
  `components.label.text` (a plain literal: the caption fully replaces the label) -- consuming
  parts 1, 2 and 3. The four metrics need genuinely different text (`cpuPct`/`{0}%` vs.
  `gpuTempC`/`{0}°`), so the composite carries the whole resolved content per choice rather than a
  token to drop into one.
- **Warn at** (`dial.json`): an ordinary `number` knob, default `"0.9"`, `sets`
  `["components.dial.threshold"]` -- part 0, the whole value, is used directly. `column-system.json`'s
  GPU-temperature copy stores `"warnAt": "0.83"` (83 °C, the RTX 3050's throttle point); its
  generator sets that, because one knob's default cannot depend on another knob's value.

### Editing widgets in the designer

There is one canvas with three depths (`DepthKind`). Enter or a double-click goes down one; Esc,
or a double-click outside the open copy or widget, comes back up.

- **Layout depth.** Copies and loose components are selected, moved, resized and deleted ("Copies",
  "What the designer writes").
- **Copy depth** (double-click a copy, Enter, or **Edit parts** in its properties): that copy's
  parts, with its knobs and overrides applied. Every change is an override on the copy, computed
  as the difference from the knob-applied widget (`Overrides.Diff`), so a value put back to the
  widget's drops its override. Orphan overrides, and overrides on a part the copy hides, are kept.
  A copy cannot gain parts. An overridden property's row menu offers **Reset to widget** (delete
  the override) and **Push to widget** (move the value into the widget and delete the override:
  every copy without its own override of that key follows). A copy selected at layout depth
  shows its knobs, **Edit widget**, **Edit parts** and **Remove widget** in the properties panel.
- **Widget depth** (Enter or double-click again, **Edit widget**, or Ctrl+Alt+K): the widget
  itself, drawn at the origin of the copy it was opened from. Every copy of that key, in every
  layout on the machine, follows. Parts (`text`, `image`, `bar`, `dial`, `line`) and values from
  the Insert and Data panels can be dropped into the frame; a drop more than 16 px outside it is
  refused. The frame hugs its parts: its `size` is their extent from `(0, 0)`, and a part moved
  above or left of the frame renormalises the parts and shifts every copy of the widget in the
  layout back by as much, so nothing on the canvas jumps. An edit that more than doubles the
  frame's area says so. Deleting a part takes the knob entries that set it with it, and the copies'
  values and overrides for it, in the same undo entry ("Orphans" under "Copies").

**Knobs** are made at widget depth: **Expose as knob** in a property's row menu (offered only for a
literal: a knob over a binding would replace it with a literal) or the **Knob** toggle on a source
setting. The knobs panel names, bounds and removes them. The designer writes only plain knobs, one
literal per knob, with one exception below; a composite, a `{token}` splice or a `=bind:` write
from a hand-written file is listed read-only and written back exactly as it was read. A knob the
designer made has no stored default: the default written is whatever its target holds when the
widget is applied, so changing the value after exposing it moves the default with it.

**The Drive knob** is the one knob the designer builds over a *binding*. A property bound to a
drive-keyed path -- `disks.drives[C].usedFraction`, `disks.drives[C].freeGB | "{0:N0} GB"` -- offers
**Expose as drive picker** instead, which makes a `drive` knob whose `sets` entry is
`components.<id>.<property>:{drive}`. A second drive-keyed property **joins the knob already there**
rather than making a second one, so one picker repoints the bar and its caption together. Only the
**saved file** carries `{drive}`: the widget on the canvas keeps the real letter so it goes on
drawing real data, and opening a saved drive widget puts the knob's default letter back.
Tokenising is idempotent, so applying twice writes the same file. Nothing else about a binding can
be made a knob; `disks.drives[0]` (an index, not a drive) is not offered.

**Routes to a widget.** In the Insert panel, a widget card's menu offers:

- **Edit**: widget depth on the first copy of it in this layout. A widget with no copy here gets a
  temporary one, removed when the canvas is back at layout depth; the widget edits stay.
- **Duplicate**: the widget under a new key, named "`<name>` copy", with one copy of it, at widget
  depth.
- **Delete**, on the owner's own widget files only: every copy of it, in every layout on the
  machine, shows as missing. On a fork of a shipped widget (its card reads "edited") the item is
  **Reset to the out-of-the-box version**: it deletes the fork, the shipped widget returns, and
  each copy keeps its own overrides.
- **New widget** (also the "+ New widget" button): an empty 172x40 widget with one copy, at widget
  depth.

**Make widget** (Ctrl+Alt+K, or the button when two or more loose components are selected) turns
loose components into a new widget in one undoable edit: the parts become relative to their
bounds, the layout sources they bind are copied into the widget, and a copy takes their place.
It is named after the first text part's literal text, else the first part's kind. With a copy (or
one of its parts) selected instead, Ctrl+Alt+K opens that copy's widget.

**Apply.** Widget edits are held in the document (`DesignerModel.WidgetEdits`) and undone with the
layout until Apply, which writes each edited widget to `%LOCALAPPDATA%\DeskWall\widgets\<key>.json`,
then the layout. Each widget file is written to a temp file and read back with
`WidgetTemplate.Load` before any is renamed into place; one that would not load (no name, more
than five knobs) stops the Apply with nothing written, because a file the daemon cannot read turns
every copy of that widget into a broken link. The shipped folder is never written: it sits beside
the exe and every install replaces it.

### Version 1: stamped instances

Read only, to migrate. A version-1 layout holds each placed widget as stamped components: ids
`"<instanceId>.<partId>"`, `rect` offset by the placement, and `widget` set to the instance id
(`ComponentDef.Widget`); and a top-level `widgets` object (`LayoutFile.Widgets`, `WidgetRecord`)
mapping each instance id (`"<widgetKey>-<n>"`) to `{ "template": "<widgetKey>", "knobs": {...},
"unlocked": false }`. Resolve and render ignore both fields; `unlocked` is parsed and ignored. In a
v2 expansion `widget` holds the copy id, which is how the designer finds a copy's parts.

`LayoutMigrator.Migrate` (`deskwall migrate`, and the designer on open) makes each record a copy:
its origin is the most common offset of its parts from the widget's own, its knobs are those that
differ from the default, and whatever else differs from the knob-applied widget is an override,
with `hidden` for a part the instance lacks. A part no override can reproduce, every component of
a widget that is missing, and any component the widget lacks stay in `components` with their v1
ids. A layout source is dropped only when no loose component binds it and the copies reproduce
it. The result is *equivalent* when its expansion reproduces the v1 components (by id, `widget`
aside), sources, and the paint order of every overlapping pair at equal `z`. `deskwall migrate`
refuses a result that is not equivalent and refuses to overwrite an existing `<file>.v1.json`
backup; `--check` prints the conversion and writes nothing.

### Starter layouts and the Arranger

The designer's canvas is free placement. `Arranger.Arrange` (`src/DeskWall.Designer/Model/Widgets/`)
is used only by `StarterGenerator` (`tests/DeskWall.Designer.Tests/Widgets/`), which builds
`column-system.json`, `clock-disks.json` and `steam-recent.json` as v2 copies;
`StarterGeneratorTests` asserts the committed files equal its output. It stacks copies in a given
order by moving their origins into a column at `ColumnX = 3220`, `ColumnWidth = 172`: `anchor:
"top"` widgets downward from `TopY = 40`, `anchor: "bottom"` widgets (`drives.json`) upward from
`BottomY = 1400`, `Gap = 16` between them, each by its expanded bounds. It resizes nothing, so an
80 px dial sits at the column's left edge. On a canvas other than 3440x1440 (`Arranger.Column`) the
column keeps its 172 px width and is right-aligned at the same proportional margin; its top,
bottom and gap scale with the height (gap at least 4 px).

`Arranger.Column` is also the designer's spawn region, drawn on an empty canvas: a widget added
from Insert lands in the first gap in the column, top down, that holds it 16 px clear of
everything; failing that, the same search one column further left, and so on; failing everywhere,
it cascades down from the top of the column in 24 px steps (`Placement.Spawn`).

## `line` (history)

`values` binds a list of records; `field` defaults to `v`. Oldest is leftmost.
`min`/`max` default to 0/1 and fix the vertical scale; out-of-range samples clamp, and a `max`
not above `min` draws nothing. Non-finite or absent samples are omitted; fewer than two samples
draws nothing. `stroke` is an ARGB colour (default `#AA9CCBEE`), `thickness` defaults to 2 px,
`glow` to 0.
`baseline: true` fills beneath the line, in `areaFill` when set and otherwise at 12% of the
stroke alpha. `glowColor` (blank is the stroke colour) and `glowStrength` (default 0.12, same
meaning as a bar's) style the halo that `glow` draws.
The content key includes normalised samples rounded to three decimals and all styling.

## Bindable geometry

Every component accepts optional `x`, `y`, `w`, `h` PropertyValues. Missing, unreadable, or
non-finite values fall back to the respective literal `rect` coordinate. Dimensions clamp at
zero. Repeater children resolve in item scope before cell clipping. Resolved geometry is
included in content keys; old and new paint bounds are restored when something moves.
Display scaling and designer zoom apply after binding resolution through retained transforms.
The designer geometry rows accept bindings just like colour and size.


An unresolved bound bar or dial fill is transparent, so absent weather or temperature data cannot
turn a conditional full-screen overlay opaque, nor paint a dial in its healthy colour. Literal
fill defaults are unchanged.

## Drawing outlines

At layout depth choose **Pen**, click points, then Enter for a stroked outline or double-click
for a closed filled shape. Escape cancels. **Trace** takes a dragged band around the skyline
and adds a shaped bar using a luminance-gradient walk with a continuity penalty. Both create
one undoable part. The photograph is fitted to the canvas before tracing.

The same Core algorithm is available without the designer:

```powershell
deskwall trace photo.jpg 0 300 3440 600 traced-layout.json
```

Coordinates are physical image pixels for the CLI. Output is a layout containing the traced bar.
The trace is a starting outline for editing; clouds and low-contrast rock can affect the result.
Rules colour rows have swatch pickers and 150 ms coalesced transient preview. Done commits one
undo entry; Escape restores the previous value.
