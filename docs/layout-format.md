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
| `effectRadius` | `6` | Blur radius in pixels for the shadow. |
| `effectColor` | `"#A0000000"` | ARGB hex for the shadow/outline/plate. |

### `image`

| Property | Default | Notes |
|---|---|---|
| `source` | required | Local path or `http(s)` URL. A remote URL is resolved through the remote image cache before drawing (`docs/sources.md`); a cache miss draws the same fallback plate as a missing local file. |
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

Ten examples, each valid against the value trees the built-in sources publish:

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
  `sqrt(sx * sy)`, so a font or corner radius does not stretch non-uniformly.
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
