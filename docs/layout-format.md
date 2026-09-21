# Layout file format

A layout is one JSON file. It names a base image, the sources it needs, and the components
placed on the canvas in physical pixels. Source of truth for this document: `LayoutFile.cs`,
`ComponentDef.cs`, `PropertyValue.cs`, `Bindings/*.cs` and `Resolve/LayoutResolver.cs` in
`src/DeskWall.Core`. Every default below is the one in the code, not a recommendation.

## Top-level file

| Property | Type | Default | Notes |
|---|---|---|---|
| `version` | int | `1` | The only version this build reads (`LayoutStore.MaxVersion`). A file with a higher version is rejected, not half-read. |
| `baseImage` | string | required | Path to a JPEG or PNG. |
| `baseFit` | `"cover"` \| `"contain"` \| `"stretch"` | `"cover"` | How the base image fills the canvas. |
| `encode` | `"jpeg"` \| `"png"` | `"jpeg"` | Output format for the composed frame. |
| `jpegQuality` | int | `92` | Passed to the WIC JPEG encoder, clamped to 1..100. Ignored when `encode` is `"png"`. |
| `sources` | array of source declarations | `[]` | See `docs/sources.md`. |
| `components` | array of components | `[]` | Drawn in `z` order (ties keep file order). |

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

## Every component: shared properties

`ComponentDef` is the base every component type inherits:

| Property | Type | Default | Notes |
|---|---|---|---|
| `id` | string | required | Unique across the resolved layout (including repeater-expanded children: `<repeaterId>[<index>].<childId>`). A duplicate throws before anything draws. |
| `rect` | `[x, y, w, h]` | required | Physical pixels, absolute in the layout's own coordinate space. Encoded as a 4-element JSON array, not an object (`RectConverter`). |
| `type` | string | required | Discriminator: `text`, `image`, `bar`, `shortcut`, `repeater`. |
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

### `bar`

| Property | Default | Notes |
|---|---|---|
| `fraction` | required | 0..1. |
| `track` | `"#46FFFFFF"` | Background fill. |
| `fill` | `"#EBFFFFFF"` | Fill below `threshold`. |
| `threshold` | `1` | At or above this fraction, `thresholdFill` is used instead of `fill`. |
| `thresholdFill` | `"#D13438"` | |
| `direction` | `"horizontal"` | `horizontal` \| `vertical`. |

Threshold colouring is a component property, not something a binding expression computes; the
spec deliberately keeps bindings free of conditionals (spec 4.2).

### `dial`

A thin arc. Same value semantics as `bar` (clamped fraction, threshold colouring), drawn as a
stroked arc centred in `rect` with radius `min(w, h) / 2 - thickness / 2` and flat caps.

| Property | Default | Notes |
|---|---|---|
| `fraction` | required | 0..1, clamped. |
| `track` | `"#46FFFFFF"` | Full-sweep arc under the fill. |
| `fill` | `"#EBFFFFFF"` | The value arc, below `threshold`. |
| `threshold` | `1` | At or above this fraction, `thresholdFill` is used instead of `fill`. A fraction of 1 is therefore at the default threshold, exactly as for `bar`. |
| `thresholdFill` | `"#D13438"` | |
| `thickness` | `6` | Stroke width in pixels. Scales with the smaller of the two display factors, not the geometric mean, because the radius comes from the short side of the rect. |
| `startAngle` | `225` | Degrees **clockwise from 12 o'clock** where the sweep begins. |
| `sweep` | `270` | Degrees of arc for fraction 1. 360 or more draws a closed ring (as two arcs; one D2D arc segment cannot describe a full turn). |

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

Every component property except `id`, `rect`, `type`, `z`, `axis`, `gap` and `slot` is a
`PropertyValue`: a JSON string/number/boolean literal, or an object `{"bind": "<binding text>"}`.
There is no third form; a property is either fixed at authoring time or fully driven by a source.

```json
"size": 64                                  // literal number
"text": { "bind": "time.now | HH:mm" }      // bound
```

## Binding grammar

A binding is a path into a source's published value tree, optionally followed by `| "<format>"`
(the quotes are optional if the format has no spaces or pipes). Grammar (`BindingParser.cs`):

    path       := name (('.' name) | '[' index ']' | '[' key ']')*
    name       := (letter | '_') (letter | digit | '_' | '-')*
    index      := non-negative integer literal
    key        := any text without ']'; looked up by the list's key field, case-insensitively as text

Twelve examples, each valid against the value trees the built-in sources publish:

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
    names a per-user absolute path. See `assets/weather/README.md` for the icon set this recipe
    expects at that path.

Two format rules matter (`Value.ToText`, spec 4.2):

- A format string containing `{0` is treated as a *composite* format and applied with
  `string.Format` to the value's raw CLR object (the underlying string, double, `DateTimeOffset`
  or bool). Anything else is applied as a plain .NET format string to the value's own
  `ToString`/`ToString(format)` (so `"N0"` works on a number, `"HH:mm"` on a time).
- A malformed format (an argument index the value does not supply, an unbalanced brace, an
  unknown type specifier) falls back to the unformatted text rather than throwing. A layout
  authoring mistake must never abort a tick.

A binding that cannot be resolved (a missing field, an out-of-range index, a key with no match,
indexing into the wrong shape of value) resolves to `null`; the bound property then falls back to
its own default (spec 3.2: staleness and missing-value handling are the component's problem, not
the binding's).

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

## Widgets

The designer's widget picker (`docs/superpowers/specs/2026-09-21-designer-widgets-design.md`
sections 3, 5, 6) adds a layer above the plain layout format described so far: a *widget
template* (`widgets/<key>.json`, shipped beside the designer exe and also read from
`%LOCALAPPDATA%\DeskWall\widgets\`) is a small, reusable recipe -- some sources, some components
in template-local coordinates starting at `(0, 0)`, and up to five *knobs* -- that gets stamped
onto a layout as one *widget instance*. The daemon and the resolver know nothing about any of
this: an instantiated widget is ordinary sources and ordinary components, plus two bookkeeping
fields (`ComponentDef.Widget`, `LayoutFile.Widgets`) both of which resolve/render ignore
entirely. The model is `DeskWall.Designer.Model.Widgets` (`src/DeskWall.Designer/Model/Widgets/`);
`WidgetRecord` itself lives in Core (`src/DeskWall.Core/Layout/WidgetRecord.cs`) only so the
source-generated JSON context can carry `LayoutFile.Widgets` without reflection.

### Template file

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

`name`, `description` and `size` (`[width, height]`) are required; `anchor` (`"top"` or
`"bottom"`, default `"top"`) and `requires` (a sentence shown on the gallery card when a
requirement -- an NVIDIA GPU, Tailscale, Steam secrets -- may be missing) are optional. The
template's key is its file name without `.json` (`WidgetTemplate.Key`), not a field in the file.
`sources` and `components` are ordinary `SourceDef`/`ComponentDef` JSON exactly as they appear in
a plain layout, except every component `rect` is relative to the widget's own `(0, 0)`, not the
canvas.

### Instantiation (`WidgetInstance.Add`)

Adding a template to a layout at an origin: components are deep-copied with `id` rewritten to
`"<instanceId>.<id>"`, `widget` set to the instance id, and `rect` offset by the origin; each
source is merged into the layout's `sources` by name (same name and same `type`: reused as-is;
same name, different `type`: the new source is added under `"<name>2"`, `"<name>3"`, ... and
every binding the *just-added* components make to the old name is rewritten to the new one --
this can only reach a component this same `Add` call is placing, never another widget's); knob
defaults are applied through `SetKnob`; and `layout.Widgets["<instanceId>"]` records the
template key and the applied knob values. The instance id is `"<templateKey>-<n>"`, `n` the
smallest positive integer not already used as an instance id in this layout.

### Knobs and the `sets` grammar

A knob's `sets` list names the paths a value change writes to, in order:

| Form | Effect |
|---|---|
| `components.<id>.<property>` | Overwrites the component property (found by `<instanceId>.<id>`, matched against `PropertySchema.For` by name) with a **literal**. |
| `components.<id>.<property>=bind:<text>` | Overwrites the property with a **binding**, parsed from the resolved value (below), not from `<text>` -- `<text>` documents the default choice's shape for a human reading the template but is never parsed. |
| `sources.<name>.settings.<key>` | Overwrites the named source's setting with a literal. |
| any of the above, with a trailing `:{token}` | Instead of overwriting, **substitutes** the literal substring `{token}` inside the target's *current* string (its own currently-authored placeholder, e.g. the weather URL's `{lat}`) with the resolved value, leaving the rest of the string as it was. |

A knob's stored value (`Knob.Default`, what a caller passes to `SetKnob`, and what
`WidgetRecord.Knobs[knobId]` keeps for showing a knob back and re-applying it) may be a **plain
string** or a **composite** of parts joined by `||` (two pipes, chosen because a binding's own
`|` format separator and every value a shipped widget writes -- URLs, format strings, captions --
use a single `|` at most). Part `0` is the whole value for a plain (non-composite) knob and the
display value for a composite one; for the `i`-th entry in `sets` (0-based), the value substituted
or written is part `i + 1` when it exists, else part `0`. This is how one knob drives several
differently-shaped targets:

- **Town** (`weather.json`): default `"Leeds||53.8008||-1.5491"`, `sets`
  `["sources.weather.settings.url:{lat}", "sources.weather.settings.url:{lon}"]`. Part 0
  ("Leeds") is what a re-opened knobs panel shows back; part 1 substitutes `{lat}`, part 2
  substitutes `{lon}` -- both into the *same* setting string, which is why the substitution form
  exists instead of a plain overwrite (an overwrite could only place one of the two numbers).
  Because `SetKnob` never makes a network call, a template's `default` for a `town` knob must
  already carry resolved coordinates; `ResolveTownAsync` (Open-Meteo geocoding, `count=1`) is
  what the designer calls to turn an arbitrary typed-in town into a fresh `"town||lat||lon"`
  value before calling `SetKnob` interactively -- it is not consulted for defaults.
- **Metric** (`dial.json`): a `choice` knob whose four `choices` are themselves full composites,
  e.g. `"GPU temperature||hardware.gpuTempFraction||hardware.gpuTempC | \"{0}°\"||gpu °C"`.
  `sets` has three entries -- `components.dial.fraction=bind:...`, `components.value.text=bind:...`,
  `components.label.text` (plain literal, no `:{token}` needed since the caption fully replaces
  the label rather than being spliced into it) -- consuming parts 1, 2 and 3 respectively. The
  four metrics need genuinely different target text (`cpuPct`/`{0}%` vs. `gpuTempC`/`{0}°`), which
  a single shared template could not express, so the composite carries the whole resolved content
  per choice rather than a token to drop into one.
- **Warn at** (`dial.json`): an ordinary `number` knob, default `"0.9"`, `sets`
  `["components.dial.threshold"]` -- no composite needed; part 0 (the whole value) is used
  directly. `column-system.json`'s GPU-temperature dial instance overrides this to `"0.83"`
  itself (`StarterGenerator`), matching the dial widget's own default for every other metric;
  the widget model has no mechanism for one knob's default to depend on another's value, so a
  metric-specific default is the instantiator's job, not the template's.

**Known limitation (a renamed source):** `sources.<name>` in a `sets` path is resolved by the
template-local name literally, not through the rename an `Add`-time source clash would have
produced for that instance. None of the fourteen shipped widgets can actually clash (each uses a
name no other shipped widget also uses, or the same name at the same type), so this only matters
if a future widget's source name collides with another already-placed widget's differently-typed
source of the same name; re-editing that knob would then write to the wrong (original) source.

**Known limitation (two instances, one source):** `MergeSources` *reuses* a source of the same
name and the same type rather than adding a second one, so two instances of the same widget share
one source. For widgets whose sources carry no knobs (`clock`, `dial`, `drives`, `uptime`, ...)
that is the point -- four dials want one `hardware` sampler, not four. For a widget whose knobs
write to `sources.<name>.settings.*` -- `command` and `headline` -- it means the two instances
fight: adding the second applies its own defaults over the first's settings, and editing either
one's knob afterwards changes what both draw. Two different commands, or two different feeds, need
the second instance's source renamed by hand in the layout file (and its component's binding with
it).

### Arranger

`Arranger.Arrange` lays every non-`Unlocked` instance out as a single vertical stack at
`ColumnX = 3220`: top-anchored instances downward from `TopY = 40`, bottom-anchored instances
(`drives.json`) upward from `BottomY = 1400`, `Gap = 16` between instances, using each instance's
current bounding box (`WidgetInstance.Bounds`, the union of its components' rects) for height --
it does not resize anything, including a narrower widget like a dial (`80` px) inside the
`ColumnWidth = 172` px column. `Unlocked` instances (an explicit opt-out, `WidgetRecord.Unlocked`)
are skipped entirely and keep whatever rect they already have. `column-system.json` and
`clock-disks.json` are generated this way, not hand-placed, which is why they are not
pixel-identical to the layouts they replace.
