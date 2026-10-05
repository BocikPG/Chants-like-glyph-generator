# GlyphGen configuration guide

GlyphGen builds a set of glyphs ("a language") from a JSON config: it grows random strokes on a lattice under your constraints, combines glyphs into composites, and renders everything to PNG.

```
GlyphGen.Cli <path-to-config.json>
```

- Property names are **case-insensitive** (the guide uses camelCase).
- Enums are written as **strings** (`"Orthogonal"`, `"RandomValid"`, ...).
- Everything is **deterministic**: one `rootSeed` drives the whole run. A glyph's seed depends only on the language name and the glyph name, so adding or removing a glyph does not change the others (unless it changes what they are compared with or combined with).

> **A note on sources.** Written from the source files, including every config class and the rendering code (layout, curve planning, typefaces, stroke building). A few small helpers (the model classes, `Vec2`, `SeedTree`) were not shown, but nothing here depends on them.

---

## Contents

1. [How a run works](#1-how-a-run-works)
2. [Root config](#2-root-config)
3. [Language: basics](#3-language-basics)
4. [Sub-symbol generation](#4-sub-symbol-generation)
5. [Guaranteed motifs](#5-guaranteed-motifs)
6. [Placement](#6-placement)
7. [Spacing](#7-spacing)
8. [Size bias by usage](#8-size-bias-by-usage)
9. [Quality gate](#9-quality-gate)
10. [Combination (composites)](#10-combination-composites)
11. [Curves](#11-curves)
12. [Rendering](#12-rendering)
13. [Output files](#13-output-files)
14. [Config validation](#14-config-validation)
15. [Recipes and interactions](#15-recipes-and-interactions)
16. [Full example](#16-full-example)
17. [Code prerequisites](#17-code-prerequisites)
18. [Schema, samples and tests](#18-schema-samples-and-tests)

---

## 1. How a run works

For each language, in order:

1. **Base glyphs** (`baseGlyphs`) are generated.
2. **Plain glyphs**: every entry of `glyphs` that is *not* a recipe key.
3. **Composites**: every entry of `glyphs` that *is* a recipe key, built by overlaying their components.

A **plain glyph** is built from:

1. its **guaranteed motifs** (if any),
2. then **`subSymbolCount`** free-form sub-symbols,

all validated against one shared geometry (crossing rules, spacing rules).

Each plain glyph then passes the **quality gate** (similarity to earlier glyphs, straight-line rejection); on failure it is regenerated with a fresh derived seed. Composites skip this gate; optionally they are compared with each other only (`quality.compositeMaxSimilarity`).

A **composite** is made by placing each component (translated, and optionally scaled/rotated) so that components don't cross each other and satisfy the spacing and bias rules. If no placement exists after all options, the least-used component is regenerated and the recipe is retried.

**Rendering** then plans curves for every glyph, frames them, builds stroke outlines and writes PNGs (section 13).

---

## 2. Root config

| Option | Type | Default | Notes |
|---|---|---|---|
| `rootSeed` | integer (ulong) | `0` | The only source of randomness; everything is derived from it. |
| `outputDirectory` | string | `"./output"` | Created if missing. |
| `languages` | array of language objects | `[]` | Each language is generated and rendered independently. |

> The old top-level `grid` was removed: put a `grid` block in each language (section 3). A leftover top-level `grid` is a config error.

---

## 3. Language: basics

```json
{
  "name": "Warrior",
  "grid": { "width": 15, "height": 15 },
  "canvas": { "sizePx": 512, "marginPx": 32 },
  "baseGlyphs": [ "you_base" ],
  "glyphs": [ "warrior", "weapon", "fortress" ]
}
```

| Option | Type | Default | Notes |
|---|---|---|---|
| `name` | string | `""` | Used for seeding and for the output folder name. |
| `grid.width`, `grid.height` | integer | 25 / 25 | **Required per language** in the JSON; each must be >= 1. Nodes sit on integer coordinates `0..width-1`, `0..height-1`. |
| `canvas.sizePx` | integer | `512` | Output image is square; minimum 16. |
| `canvas.marginPx` | integer | `32` | Space around the drawing (stroke thickness is added on top). |
| `glyphs` | array of names | `[]` | The language's real glyphs, in the atlas/contact sheet. |
| `baseGlyphs` | array of names | `[]` | Glyphs that exist only as recipe components. See below. |

**Glyph names** are plain strings (`"warrior"`). The older form `{ "name": "warrior" }` still loads.

**Base glyphs**

- Generated first, so recipes can reference them.
- Rendered like everything else (same language-wide framing, same debug images), but written to `<language>/_bases/` and **left out of the main `_contact-sheet.png`** (they get a sheet of their own, see `render.contactSheet`).
- Pass through the same quality gate as any other glyph.
- Must not also appear in `glyphs`, and must not be recipe keys.

---

## 4. Sub-symbol generation

A glyph consists of `subSymbolCount` sub-symbols. Each is a single chain (a self-avoiding walk) of nodes on the grid, grown one move at a time. If no valid move exists, the chain ends early and that attempt fails; placement then retries (section 6).

```json
"subSymbolCount": { "min": 1, "max": 3 },
"subSymbolLength": { "min": 6, "max": 8 },
"connectionRules": [
  { "direction": "Orthogonal", "minLength": 1, "maxLength": 5, "weight": 3.0 },
  { "direction": "Diagonal",   "minLength": 1, "maxLength": 5, "weight": 1.0 }
],
"allowNodeRevisit": true,
"allowEdgeRevisit": true,
"allowClosedLoop": false,
"closedLoopWeight": 0.0,
"allowSymbolsToCross": true
```

| Option | Default | Notes |
|---|---|---|
| `subSymbolCount` `{min,max}` | `1..1` | Sub-symbols per glyph (in addition to guaranteed motifs). |
| `subSymbolLength` `{min,max}` | `3..6` | **Node count per chain, including the start node.** |
| `connectionRules[]` | `[]` | The allowed moves, see below. At least one is needed to grow a chain. |
| `allowNodeRevisit` | `false` | A chain may step onto a node it already visited. |
| `allowEdgeRevisit` | `false` | A chain may exactly retrace an earlier edge. When allowed, a retrace skips the geometry check. |
| `allowClosedLoop` | `false` | Allows a move back to the chain's start node, ending the chain (needs at least 3 nodes). A closed chain counts as complete even if shorter than the target. |
| `closedLoopWeight` | `0.0` | Weight of the loop-closing move against the other candidate moves. |
| `allowSymbolsToCross` | `false` | If `false`, no edge may cross an already-placed edge of any sub-symbol or motif in the glyph. Edges that merely **meet at a shared node** (end to end, or a loop closing on its start) do not count as crossing. A true crossing, a node landing on another edge's interior (a T-junction) and a collinear overlap beyond a single point all do. Passing over a node does not occupy it. |

`subSymbolCount` and `subSymbolLength` are integer ranges, **inclusive at both ends** (`1..3` can give 1, 2 or 3), chosen uniformly with the glyph's seeded random generator. If `max` is smaller than `min`, the result is simply `min`. Percent and float ranges used elsewhere (`straightLineBias`, widths, angles, jitter) are continuous and uniform between `min` and `max`.

### Connection rules

| Field | Notes |
|---|---|
| `direction` | `Orthogonal` (4 directions, the default), `Diagonal` (4 diagonals) or `Both`. |
| `minLength`, `maxLength` | Step length in grid cells along the direction. Both default to `1` (adjacent); minimum effective value 1. A diagonal step of length *n* spans *n* cells on each axis. |
| `weight` | Relative probability of choosing this rule (default `1.0`). A language with a single rule uses it every time. |

At each step every geometrically valid concrete move (every direction x length of every rule) is listed, and one is picked by weight. By default (`connectionWeighting: "PerMove"`) **each concrete move inherits its rule's full weight**, so a rule with a wide length range or `Both` directions gets more total probability than a narrow rule with the same `weight`. With `"PerRule"` a rule's weight is split evenly across its valid moves at that step, so the weights mean what they say: two rules of equal weight are each picked about half the time, however many moves each has (a rule with no valid move at that step drops out). The setting is per language and also applies to generative motifs' rules.

A chain **never crosses itself**, whatever `allowSymbolsToCross` says; that flag controls crossing *other* shapes.

---

## 5. Guaranteed motifs

Motifs are shapes placed in **every** plain glyph, before the free-form sub-symbols. They obey the same crossing and spacing rules.

> Fill in exactly one of `literal` / `generative`, matching `type`; the loader rejects a missing block, both blocks, and malformed literal points (section 14).

```json
"guaranteedMotifs": [
  {
    "id": "crossbar",
    "type": "Literal",
    "literal": { "nodes": [ [0, 0], [2, 0], [2, 2] ] },
    "placement": { "mode": "RandomValid", "allowRotationOnPlace": true, "maxPlacementAttempts": 20 }
  },
  {
    "id": "tail",
    "type": "Generative",
    "generative": {
      "lengthRange": { "min": 3, "max": 4 },
      "connectionRules": [ { "direction": "Orthogonal", "minLength": 1, "maxLength": 2, "weight": 1.0 } ],
      "allowClosedLoop": false,
      "closedLoopWeight": 0.0
    },
    "placement": { "mode": "RandomValid", "maxPlacementAttempts": 30 }
  }
]
```

| Field | Notes |
|---|---|
| `id` | Identifies the motif; its nodes are drawn blue in debug renders. |
| `type` | `Literal` or `Generative`. |
| `literal.nodes` | Fixed shape: a list of integer `[x, y]` pairs, relative to the placement origin, joined in order as a polyline. Rotated about `(0,0)` (if rotation applies), then translated to the origin. |
| `generative.lengthRange` | Node count of the generated chain (default `3..6`). |
| `generative.connectionRules` | Same schema as the language's `connectionRules` (default: none, so set at least one). |
| `generative.allowClosedLoop`, `generative.closedLoopWeight` | Loop-closing settings for this motif only (default `false` / `0`). |
| `placement` | A placement block, same schema as `subSymbolPlacement` (section 6). |

A generative motif uses its own length, rules and loop settings, but takes `allowNodeRevisit` / `allowEdgeRevisit` from the language.

If a motif cannot be placed within its attempt budget, generation fails with an error naming it. A `Literal` motif with `Fixed` placement has nothing that varies, so it gets a single attempt regardless of `maxPlacementAttempts`.

---

## 6. Placement

`subSymbolPlacement` positions free-form sub-symbols; each guaranteed motif has its own `placement` block of the same shape.

```json
"subSymbolPlacement": {
  "mode": "RandomValid",
  "fixedOrigin": null,
  "allowRotationOnPlace": true,
  "maxPlacementAttempts": 20,
  "startOnExistingNode": false
}
```

| Option | Default | Notes |
|---|---|---|
| `mode` | `RandomValid` | `RandomValid`: random grid point per attempt. `Fixed`: always `fixedOrigin`; leaving it unset in this mode throws an error when generation reaches it. `CenterAnchor`: exactly the grid center (`width / 2`, `height / 2`, integer division). Every mode is still collision- and spacing-checked. |
| `fixedOrigin` | `null` | A grid point, required for `Fixed` (and an error with any other mode). Written as `{ "x": 7, "y": 9 }` or `[7, 9]`, and it must lie inside the grid. (A converter was needed for this: without it the point silently came out as (0,0).) Literal motif nodes are `[x, y]` arrays too. |
| `allowRotationOnPlace` | `true` | Allows a random rotation per attempt. Only **Literal motifs** are affected (see the rotation note below). |
| `rotations` | `null` | Angles this placement may rotate by: a list of `0`, `90`, `180`, `270`. `null` = inherit `combination.rotation.allowed` (when `combination.allowRotate` is on), as before; a list = use exactly these; `[]` = never rotate. Only valid on a **Literal** motif's placement (the loader rejects it elsewhere, and with `allowRotationOnPlace: false`). |
| `maxPlacementAttempts` | `20` | Attempts per shape. Only used when something varies between attempts (random position, rotation, or a generated shape); otherwise one attempt. Free-form sub-symbols always vary. |
| `startOnExistingNode` | `false` | New shapes start on a node already in the glyph. See below. |

Each attempt resolves an origin, asks the shape to build itself there, then checks grid bounds, crossing and spacing. The first attempt that passes wins.

**Rotation note.** Rotation changes only **Literal motifs**: a generated chain ignores the rotation value (it is already random), so for free-form sub-symbols and generative motifs `allowRotationOnPlace` has no visible effect. A Literal motif's angles come from its placement's own `rotations` list; without one they come from `combination.allowRotate` / `combination.rotation.allowed` (section 10), which is the original coupling.

### `startOnExistingNode`

Instead of a random grid point, the origin is drawn from the nodes already committed in the glyph (motifs included), so shapes grow out of each other. The first shape, with nothing to attach to, is placed normally.

Requirements (the loader rejects violations):

- `mode` must be `RandomValid`.
- `subSymbolSpacing.minNodeDistance` must be `0` when spacing is enabled (a shared node is at distance 0, so every attempt would be rejected).

It works with `allowSymbolsToCross` either way: edges that only meet at a shared node don't count as crossing (see section 4), so a shape can grow out of an existing node even with crossing off. A start that would run along an existing edge, or cut through one, is still rejected by the normal checks and retried.

---

## 7. Spacing

Block: `subSymbolSpacing`. Disabled by default, because each constraint makes placement harder.

```json
"subSymbolSpacing": {
  "enabled": true,
  "minCenterDistance": 1,
  "minNodeDistance": 0.1,
  "maxNodeDistance": 4,
  "maxDistanceMode": "NearestShape",
  "selfSpacing": [
    { "mode": "Adjacent", "minDistance": 1.5, "lookback": 2 },
    { "mode": "AllNodes", "maxDistance": 6 }
  ]
}
```

Distances are in grid units (Euclidean), `0` means "off". Nothing here overrides a hard constraint; failing candidates are simply discarded and retried.

### Between shapes

A "shape" is a motif or a free-form sub-symbol, and in composites a whole placed component (all its chains pooled). A candidate is checked against **every** shape already placed.

| Option | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Master switch for this whole block, including `selfSpacing`. |
| `minCenterDistance` | `0` | Minimum distance between the centroids (average node position) of two shapes. |
| `minNodeDistance` | `0` | Minimum distance between the **closest pair of nodes** of two different shapes. |
| `maxNodeDistance` | `0` | Maximum distance between the closest node pair of the candidate and an existing shape. Must be >= `minNodeDistance` when both are set. |
| `maxDistanceMode` | `NearestShape` | `NearestShape`: within range of **at least one** existing shape (keeps the glyph connected). `AllShapes`: within range of **every** existing shape. |

The first shape in a glyph has nothing to be near, so the maximum doesn't apply to it.

### Within one chain: `selfSpacing`

A list of rules applied by the chain generator **as each node is added**, so they steer growth instead of rejecting finished chains. Several rules (even of the same mode) can be active; a new node must satisfy all of them. Only for generated chains: literal motifs and combination components are not affected.

| Rule field | Default | Notes |
|---|---|---|
| `mode` | `Existing` | Which earlier nodes the new node is measured against (below). |
| `minDistance` | `0` | Minimum distance, `0` = off. |
| `maxDistance` | `0` | Maximum distance, `0` = off. Must be >= `minDistance` when both are set. |
| `lookback` | `1` | `Adjacent` only: how many of the chain's most recent nodes are checked (must be >= 1). |

| Mode | Minimum applies to | Maximum applies to |
|---|---|---|
| `Existing` | every earlier node **except** the one it connects from, and except an exact revisit of an existing node | every earlier node (connecting-from node included) |
| `Adjacent` | only the last `lookback` nodes (1 = just the node it connects from, 2 = also the one before). An exact revisit is exempt. Effectively sets a minimum step length / turn size. | the same nodes |
| `AllNodes` | **every** earlier node, no exemptions. The connecting step must itself be at least this long, and revisiting a node becomes impossible (distance 0). | every earlier node (same as `Existing`) |

Notes:

- Distances are node-to-node only; a node can still come close to the middle of one of its own edges.
- Large minimums cause more dead ends (shorter chains, more retries); raise `maxPlacementAttempts` or the connection-rule length ranges if needed.
- A maximum smaller than your shortest connection step stops chains from growing (a diagonal step of length *n* is *n*·sqrt(2) long).
- `Existing` and `AllNodes` with the same minimum overlap: `AllNodes` is the stricter. Combining them is useful only with different values.

---

## 8. Size bias by usage

Block: `usageSizeBias`. Counteracts a size skew towards heavily-used component glyphs: the more recipes a plain glyph appears in, the likelier it rolls **low** values for its sub-symbol count and lengths.

```json
"usageSizeBias": {
  "strength": 0.5,
  "affectSubSymbolCount": true,
  "affectSubSymbolLength": true
}
```

| Option | Default | Notes |
|---|---|---|
| `strength` | `0` | Extra draws added **per use**. `0` = off (generation is unchanged). Must be a finite number >= 0. |
| `affectSubSymbolCount` | `true` | Bias the number of sub-symbols. |
| `affectSubSymbolLength` | `true` | Bias each sub-symbol's length. |

How it works: "uses" = how many times the glyph's name appears across `combination.recipes`. A glyph with *u* uses rolls its count/length `1 + strength × u` times and keeps the **lowest** result (a fractional part is applied as a probability). The result always stays inside the configured `min..max`.

Rough feel for a 3-value range and a glyph used twice: `strength: 0.5` gives the lowest value ~55% of the time (33% without bias); `strength: 1` gives ~70%.

Not affected: glyphs used in no recipe, guaranteed motifs (they have their own length range), and which component gets regenerated.

**Why it exists.** When a composite can't be placed, the least-used component is regenerated. Rarely-used glyphs are therefore re-rolled until they fit, which favours smaller shapes, while heavily-used glyphs stay at their original, larger size.

Turning this on changes the random draws of biased glyphs, so those glyphs will look different from before.

---

## 9. Quality gate

Block: `quality`. Applied to every plain glyph (base glyphs included) against every glyph accepted earlier in the language. A failing glyph is regenerated with a fresh derived seed up to `maxRegenerationAttempts` times, then the run fails with an error naming the glyph. **Composites skip this gate** (they contain their components by construction).

```json
"quality": {
  "maxSimilarity": 0.7,
  "rejectStraightLineOnly": true,
  "maxRegenerationAttempts": 10
}
```

| Option | Default | Notes |
|---|---|---|
| `maxSimilarity` | `0.7` | Jaccard similarity (0-1) at or above which two glyphs count as too alike. `1.0` or higher disables the check. |
| `rejectStraightLineOnly` | `true` | Reject a glyph whose whole geometry is one straight line. |
| `maxRegenerationAttempts` | `10` | Attempts per glyph (or per composite, see below). |
| `compositeMaxSimilarity` | `1.0` | Similarity limit for **composites**, compared **only with other composites**, never with plain or base glyphs. `1.0` or more = composites aren't compared (the default). A composite at or above the limit is rebuilt, up to `maxRegenerationAttempts`: each retry drops the preference for the plain (0,0) overlay, so the same components come out arranged differently. Composites are never subject to `rejectStraightLineOnly`. |

Similarity is the Jaccard index of the two glyphs' node sets after shifting each so its bounding box starts at the origin. It is **not** rotation-, mirror- or scale-invariant: the same shape rotated or mirrored is not flagged.

---

## 10. Combination (composites)

Block: `combination`. A composite is a glyph whose name is a key in `recipes`; it is built by overlaying its component glyphs.

```json
"combination": {
  "recipes": { "fortress": [ "warrior", "protect" ] },
  "allowScale": true,
  "allowedScales": [
    { "numerator": 1, "denominator": 2, "weight": 1.0 },
    { "numerator": 2, "denominator": 1, "weight": 1.0 }
  ],
  "allowRotate": true,
  "rotation": { "allowed": [ 0, 90, 180, 270 ], "weight": 1.0 },
  "maxRetriesPerRecipe": 5,
  "bias": { "modes": [ "Shortest", "Narrowest" ], "includeScaleAndRotation": false }
}
```

| Option | Notes |
|---|---|
| `recipes` | Map of composite name to a list of component names (plain glyphs, base glyphs, or other composites). The composite must be listed in `glyphs`. A recipe built from another composite needs that composite listed **before** it in `glyphs` (composites are generated in list order); the loader enforces this, which also rules out circular recipes. A nested recipe (a composite built from another composite) has been run end to end and works. |
| `allowScale` | Default `false`. Allow scaled placements as a fallback. Does nothing while `allowedScales` is empty. |
| `allowedScales[]` | Default empty. Each item is `{ numerator, denominator, weight }`, defaulting to `1`, `1`, `1.0`. Shrinking (`< 1`) is only used when every node coordinate scales to a whole number (lattice-safe); enlarging always is. `weight` orders the fallback search: heavier factors are tried earlier, and `0` excludes the factor. Keep numerator and denominator positive: a `denominator` of 0 divides by zero when a scale is tried. |
| `allowRotate` | Default `false`. Allow rotated placements as a fallback. Does nothing while `rotation.allowed` is just `[0]`. |
| `rotation.allowed` | Default `[0]`. Allowed rotation angles in degrees. **Only `0`, `90`, `180` and `270` are supported**; any other angle (including 45, which an old code comment mentioned) is rejected by the loader. `weight` (default `1.0`) is one value shared by every non-zero angle: how likely a rotated placement is compared with an unrotated one (1 = equally likely, 0.5 = half as likely, 0 = never rotate). It multiplies with the scale factor's weight. |
| `maxRetriesPerRecipe` | Default `5`. How many attempts (each followed by regenerating a dependency) before the recipe fails. |
| `allowComponentsToCross` | Default `false`. If `true`, a composite's components may cross each other (they can always touch at shared nodes). Independent of the language's `allowSymbolsToCross`, which governs strokes inside one glyph. Spacing rules still apply. |
| `bias` | Optional shape bias, below. |

### Placement search

For each component, in order:

1. **Identity first**: scale 1/1, rotation 0, every translation that keeps the shape on the grid, shuffled; the (0,0) overlay is tried before the others.
2. If nothing fits, every allowed **scale x rotation** combination (shuffled) is tried, each with its full translation search.
   The order of these combinations is weighted: each combination's weight is its scale factor's `weight` times (for a non-zero angle) `rotation.weight`, so heavier ones tend to be tried first, and weight 0 removes one. With every weight equal it is a plain uniform shuffle, as before.
3. Only when everything is exhausted is a dependency regenerated: the component used by the fewest composites built so far goes first (ties follow recipe order), cycling through the recipe's components on later attempts. The regenerated glyph replaces the canonical one, and every other recipe using it is rebuilt to match.

Every candidate must satisfy: all nodes inside the grid, **no crossing** with already-placed components (unless `allowComponentsToCross` is on; it ignores the language's `allowSymbolsToCross`), and the language's `subSymbolSpacing` rules (each component counts as one shape). Components may touch at shared nodes (that isn't crossing); a T-junction or a collinear overlap is.

### Bias

Steers composites towards an overall shape. It is a **ranking only**: it chooses among placements that already pass every constraint, never relaxes one, and if only one placement is legal it is used.

| Option | Default | Notes |
|---|---|---|
| `bias.modes` | `[]` | Empty = off (first legal placement is used). Any of `Tallest`, `Shortest`, `Widest`, `Narrowest`, `Squarest`. |
| `bias.includeScaleAndRotation` | `false` | `false`: bias picks only among translations, keeping the translate-first order. `true`: every allowed scale and rotation competes too, so `Tallest` could choose a larger scale or a 90-degree turn. |
| `bias.weights` | `{}` | Optional weight per listed mode, e.g. `{ "Tallest": 3 }` to care three times as much about height as about the other modes (any mode not mentioned weighs 1). Only the ratios matter. Weights for modes not in `modes` are rejected, as is giving every listed mode weight 0. |

- "Size" is the bounding box of the combined node positions, in grid units.
- Several modes can be combined; each contributes a score relative to the grid size and the scores are added with equal weight. Examples: `Shortest` + `Narrowest` = smallest box; `Tallest` + `Widest` = biggest; `Tallest` + `Narrowest` = tall and thin; `Squarest` + `Shortest` = compact and square.
- `Tallest` with `Shortest`, or `Widest` with `Narrowest`, cancel out and are rejected.
- With 3 or more components the bias is greedy: each is placed to best serve the shape so far.

---

## 11. Curves

Block: `curve`. Grid edges are always straight in the generated topology; the renderer turns each edge into a straight line or a cubic bezier. Curves are planned from the seed and can in principle bulge into neighbouring strokes; the tension cap and the proximity guard make that rare but do not rule it out.

```json
"curve": {
  "mode": "Derived",
  "contextMode": "AllCurved",
  "straightLineBias": { "min": 50, "max": 90 },
  "tension": {
    "options": [
      { "min": 0,   "max": 0,   "weight": 3 },
      { "min": 0.4, "max": 0.5, "weight": 2 }
    ]
  },
  "explicitJitterDegrees": { "min": 20, "max": 20 },
  "proximityGuard": { "enabled": true, "minEdgeLength": 2, "minDistanceToOtherNodes": 1 }
}
```

| Option | Notes |
|---|---|
| `mode` | Default `Derived` (tangent from the neighbouring nodes, smooth); `Explicit` (in/out handles deviate independently, kinks possible); `Hybrid` (each node randomly Derived or Explicit). |
| `straightLineBias` `{min,max}` | Default `0..0` (nothing forced straight). Percentage 0-100, resolved once per glyph, then used as the **per-edge probability** of being straight. A soft hint: the realised share only approximates it. |
| `tension.range` `{min,max}` | Default `0.25..0.35`. Used **only when `options` is empty**: one continuous range, sampled uniformly. |
| `tension.options[]` | Default empty. Weighted bands `{min, max, weight}` (weight default `1`); one band is picked per node handle, then a value is sampled inside it. This is what allows bimodal looks such as "70% dead straight (`0..0`), 30% strongly curved (`0.4..0.5`)". When non-empty it replaces `range`. The final value is clamped to `0..0.5` and is the handle length as a fraction of the edge length. |
| `explicitJitterDegrees` `{min,max}` | Default `20..60`. Maximum angular deviation of explicit handles, in degrees, drawn per node. Only matters in `Explicit`/`Hybrid` modes. |
| `proximityGuard.enabled` | Default `false`. Forces some edges straight to reduce bulging into neighbours; it changes how many edges end up curved. |
| `proximityGuard.minEdgeLength` | Default `1.5`. Edges shorter than this (grid units) are forced straight. |
| `proximityGuard.minDistanceToOtherNodes` | Default `0` (off). An edge is forced straight when any other node in the glyph (any chain or component) lies closer than this to either endpoint. Broader and more expensive than `minEdgeLength`. |
| `recheckProximityInCombined` | Default `false`. A component reuses its source glyph's curves, so the guard only ever judged it alone and at its original size. When `true`, the guard is re-run once the component sits in a composite (scaled edge lengths, the composite's other nodes) and any curved edge it would force straight is flattened. Needs `proximityGuard.enabled`. |
| `forceAllEdgesCurved` | Default `false`. Plans every edge as a curve, ignoring `straightLineBias` and the guard, so "curved" really means curved everywhere. Whether an edge visibly bends still depends on tension (a `0..0` band gives a straight-looking curve). Must not be combined with a non-zero `straightLineBias` or an enabled guard (the loader rejects it). |
| `contextMode` | Default `AllCurved`. Curved or straight, for glyphs on their own versus inside composites (below). |

### `contextMode`

"Mini" = a plain glyph rendered on its own. "Combined" = a glyph's components inside a composite (a composite rendered on its own also counts as combined).

| Value | Mini | Combined |
|---|---|---|
| `AllCurved` (default) | curved | curved |
| `AllStraight` | straight | straight |
| `CurvedMiniStraightCombined` | curved | straight |
| `StraightMiniCurvedCombined` | straight | curved |

- "Curved" means the glyph's normal curve plan: `straightLineBias` still decides edge by edge which edges are lines. It does **not** force every edge to bend.
- "Straight" makes every edge a straight line.
- Wherever a glyph is curved, its curves are the **same** in both contexts: a component reuses its source glyph's plan, mapped through the component's scale, rotation and translation.
- Reused curves are, by default, not re-checked against neighbouring components, so a bulge can reach into an adjacent component. `recheckProximityInCombined` re-runs the guard there; `CurvedMiniStraightCombined` avoids the problem altogether.
- If a component's source glyph can't be matched to its stored geometry, that layer falls back to being planned from the composite's own seed.

---

## 12. Rendering

Block: `render` (pixel size and margin come from `canvas`). All of a glyph's ink is filled as **one path**, so overlaps and crossings come out solid even with a semi-transparent colour.

```json
"render": {
  "framing": "Language",
  "foreground": "#FFFFFFFF",
  "background": "#00000000",
  "debugShowNodes": false,
  "debugColorComponents": false,
  "typeface": {
    "kind": "Rectangular",
    "perGlyphJitter": 0.0,
    "rectangular": {
      "width": { "min": 0.045, "max": 0.045 },
      "join": "Miter",
      "cap": "Butt",
      "miterLimit": 4.0
    }
  }
}
```

| Option | Notes |
|---|---|
| `framing` | How glyphs are scaled and positioned on the canvas (default `Grid`): `Grid` = the whole grid maps to the canvas, so a glyph's size and position on the grid carry through; `Glyph` = crop to each glyph's own bounding box and centre it (relative sizes are lost); `Language` = crop to the combined bounding box of every glyph in the language, one shared scale for all; `GlyphCentered` = the grid's scale, but each glyph re-centred on the canvas. |
| `foreground`, `background` | `#RRGGBB` or `#AARRGGBB`. Defaults: foreground `#000000`, background `#00000000` (transparent). Unparseable values fall back to black / transparent. |
| `debugShowNodes` | Debug option. With this or `debugColorComponents` on, the CLI writes an extra `<glyph>.debug.png` next to each normal image, drawn with the enabled overlays; the normal images stay clean, and with both off no debug files are written. This one draws the faint lattice with coordinate labels every 5 cells, plus node dots: red = free-form sub-symbol, blue = motif, **gold = a node used more than once** (a revisit or a node shared by shapes). |
| `debugColorComponents` | Debug option (see above): in the `.debug.png` images, fills each combination component in its own colour. |
| `contactSheet.columns` | Default `6`. Glyphs per row on the contact sheet(s); must be >= 1. |
| `contactSheet.baseGlyphsSheet` | Default `true`. Also writes `_bases/_contact-sheet.png` with the base glyphs (only when `baseGlyphs` is used); the main sheet never contains them. |
| `typeface.kind` | `Rectangular` (default), `ThickEnds` or `Brush`. Selects which sub-block below is used; a missing sub-block means that typeface's defaults. |
| `typeface.perGlyphJitter` | `0` (default) = every glyph uses the identical typeface; `0.1` = each glyph's widths vary by up to +-10%. Clamped to 0-0.9. |

### Framing and padding

- The grid-to-pixel mapping is **uniform** (one scale for both axes), so shapes are never distorted. `framing` (above) chooses what gets scaled and centred.
- The drawing is fitted to the canvas minus a padding on every side: `canvas.marginPx` plus the typeface's largest ink extent (including `perGlyphJitter`) plus 1 px, so strokes at the edge don't clip. Bounds come from the actual sampled curves, not just the nodes.
- A minimum extent of one grid unit is used when scaling, so a lone straight line isn't blown up without limit.
- Stroke widths are fixed in pixels (canvas fraction x canvas size), so framing never changes stroke weight: a zoomed-in crop has the same thickness as a full-grid view.

### Joins and caps

Ink is built from many small convex pieces (one quad per segment, plus a wedge or fan per join and cap) filled together, so overlaps and crossings come out solid.

| `join` | Look |
|---|---|
| `Miter` | Sharp pointed corner. Falls back to a bevel when the tip would be longer than `miterLimit` x half-width. |
| `Bevel` | Corner cut flat. |
| `Round` | Rounded corner (a full reversal also gets a round cap). |
| `Overlap` | No join at all: plain rectangles crossing, notch and all. |

Caps (`Rectangular` only; the other typefaces choose their own end shapes): `Butt` ends flat at the node, `Square` extends half a width past it, `Round` ends in a half disc.

Joins are normally only computed between two segments of the same chain. Where two **different** open chains end on the same node (sub-symbols or components sharing a node), the renderer builds a real join between them with the typeface's own `join` and `miterLimit`, so a Miter typeface still gets the sharp ">" there.

### Typefaces

One typeface per **language**, resolved once from the seed, so every glyph reads as the same font. All widths are **fractions of the canvas size**. Width-like options are `{min,max}` ranges, resolved once per language. `perGlyphJitter` then scales every width of a glyph by `1 + jitter x r`, with `r` drawn per glyph in -1..1. Typefaces don't depend on `curve.mode`: on a curved edge you simply get the stroke following the curve.

**`Rectangular`** (`typeface.rectangular`): constant-width strokes.

| Option | Default | Notes |
|---|---|---|
| `width` | `0.05` | Stroke width. |
| `join` | `Miter` | See the join table. |
| `cap` | `Butt` | `Butt`, `Square` or `Round`. |
| `miterLimit` | `4.0` | Miter tip length / half-width above which a miter becomes a bevel. |

**`ThickEnds`** (`typeface.thickEnds`): a body of one width flaring to another at chosen nodes. If `endWidth` is smaller than `width`, the ends pinch instead of flaring.

| Option | Default | Notes |
|---|---|---|
| `width` | `0.03` | Width along the body of the stroke. |
| `endWidth` | `0.06` | Width at flared nodes. |
| `flareLength` | `0.25` | Flare length as a fraction of **each edge's own length** (capped at 0.5), so long or scaled edges flare proportionally. |
| `profile` | `Smooth` | How the width moves along the flare: `Linear`, `Smooth` (smoothstep) or `Sharp` (cubic: stays thin until close to the node). |
| `appliesTo` | `ChainEnds` | Which nodes flare: `ChainEnds` = the free tips of open chains (closed loops have no tips, so they stay uniform); `EveryEdge` = both ends of every edge; `ChainEndsAndCorners` = tips plus real corners (direction change of roughly 10 degrees or more; a closed loop flares at its seam if that is a corner). |
| `terminal` | `Flat` | End shape of open chains: `Flat` (butt), `Round` (round cap) or `Ball` (a disc on each open tip). |
| `ballScale` | `1.3` | Ball diameter = `endWidth` x `ballScale` (`Ball` only; closed loops get none). |
| `join` | `Round` | See the join table. |
| `miterLimit` | `4.0` | As above. |

**`Brush`** (`typeface.brush`): `mode` picks the look, and each mode reads its own options.

| Mode | How it draws | Options it reads |
|---|---|---|
| `Taper` | Pointed at both ends, swelling in the middle: width follows `sin(pi x s) ^ taperSharpness` where `s` is the fraction along the whole **chain**. Closed loops are uniform at full width. Round caps if the tip is over half a pixel, otherwise butt. | `width`, `tipWidth`, `taperSharpness` |
| `BroadNib` | Calligraphy pen: width depends on stroke direction against a fixed nib angle, `width x abs(sin(direction - nibAngle))`, never thinner than `thinWidth`. Butt caps. | `width` (nib length), `nibAngleDegrees`, `thinWidth` |
| `Ink` | Pressure profile along the stroke, per-side seeded edge wobble, optional dry-brush ragged edges, round caps, and a blob at the start of each open stroke. Seeded per chain, so it is repeatable. | `width`, `pressureVariation`, `edgeWobble`, `raggedness`, `startBlobScale` |

| Option | Default | Notes |
|---|---|---|
| `mode` | `Taper` | `Taper`, `BroadNib` or `Ink`. |
| `width` | `0.06` | Maximum stroke width (`Taper` / `Ink`) or nib length (`BroadNib`). |
| `tipWidth` | `0` | `Taper`: width at the tips. |
| `taperSharpness` | `1.0` | `Taper`: above 1 = pointier, below 1 = fuller (minimum 0.05). |
| `nibAngleDegrees` | `40` | `BroadNib`: angle on screen, clockwise from the +x axis. |
| `thinWidth` | `0.012` | `BroadNib`: width of the thin strokes. |
| `pressureVariation` | `0.35` | `Ink`: how much the width varies along a stroke (0-1). |
| `edgeWobble` | `0.15` | `Ink`: wobble of each edge. |
| `raggedness` | `0` | `Ink`: roughness of the edges. |
| `startBlobScale` | `1.4` | `Ink`: above 1 adds a blob where each open stroke starts. |
| `join` | `Round` | See the join table. |
| `miterLimit` | `4.0` | As above. |

Example of the other kinds:

```json
"typeface": { "kind": "ThickEnds", "thickEnds": { "width": { "min": 0.03, "max": 0.03 }, "endWidth": { "min": 0.07, "max": 0.07 }, "terminal": "Ball" } }
"typeface": { "kind": "Brush", "brush": { "mode": "BroadNib", "nibAngleDegrees": { "min": 40, "max": 40 } } }
```

The CLI writes `.debug.png` files only when `debugShowNodes` or `debugColorComponents` is on, alongside the normal (always clean) images, and each shows just the overlays that are enabled.

---

## 13. Output files

```
<outputDirectory>/
  <Language>/
    warrior.png              plain glyphs and composites
    warrior.debug.png        only when debugShowNodes / debugColorComponents is on
    fortress.png
    _contact-sheet.png       real glyphs only (render.contactSheet.columns, default 6)
    _bases/
      you_base.png           base glyphs (only if baseGlyphs is used)
      you_base.debug.png     (debug options only)
      _contact-sheet.png     the base glyphs (render.contactSheet.baseGlyphsSheet)
```

- The CLI prints a config problem as "Invalid config:" with one problem per line (JSON syntax errors and unreadable files get their own messages) and exits with a non-zero code on any failure.
- File and folder names are sanitized (characters invalid in file names become `_`).
- The contact sheet lists real glyphs in the order of `glyphs`, each cell at the glyph's own pixel size with a light-grey frame and 8 px gaps, no labels. The column count comes from `render.contactSheet.columns`. If `render.background` is transparent or translucent, the sheet uses white so glyphs stay visible.
- The console also prints an ASCII preview of every glyph (straight lines only), marking base glyphs.
- PNGs can differ slightly between platforms and Skia versions because of anti-aliasing; geometry is deterministic, but don't expect byte-identical files.

---

## 14. Config validation

`RootConfigLoader` works in three steps:

1. **Parse.** Malformed JSON, or a value of the wrong type (a string where a number belongs), stops here with a parse error.
2. **Structure** (checked on the raw JSON): unknown properties, with a "Did you mean ...?" hint (a typo such as `subSymbolSpacin` used to be silently dropped); explicit `null` where a value is required; the `grid` block in each language. These are reported on their own, first.
3. **Values**: everything below. All problems are reported together, one per line, as `[Language] path: message`, with options named the way the JSON spells them (`selfSpacing[0]: maxDistance (1) is smaller than minDistance (3)`).

A top-level `"$schema"` key is accepted and ignored, so a config can point at `config.schema.json` (section 18).

| Area | Rules |
|---|---|
| Top level | `outputDirectory` not empty; at least one language; language names non-empty and unique (case-insensitive: they pick the folder and the seeds). |
| Canvas | `sizePx` >= 16; `marginPx` >= 0 and under half of `sizePx`. |
| Grid | A `grid` block in every language (no top-level one); width and height >= 1. |
| Glyph names | Non-empty, unique across `glyphs` and `baseGlyphs`. |
| Recipes | The key is in `glyphs` and not a base glyph; at least one component; every component listed; a composite used as a component is listed before its user (rules out cycles); `maxRetriesPerRecipe` >= 1. |
| Scales and rotation | `numerator` and `denominator` > 0; weights finite and >= 0; angles only 0, 90, 180, 270. |
| Sub-symbols | `subSymbolCount` min >= 0, `subSymbolLength` min >= 2 (a stroke needs two nodes), `min` <= `max`; `closedLoopWeight` >= 0. |
| Connection rules | At least one when `subSymbolCount.max` > 0 (always for a generative motif); `minLength` >= 1; `maxLength` >= `minLength`; weights finite, >= 0 and not all 0. |
| Guaranteed motifs | Unique, non-empty `id`; `type` matches exactly one of `literal` / `generative`; a literal has at least 2 points, each an `[x, y]` pair, with no point repeated back to back; a generative `lengthRange` min >= 2. |
| Placement (all blocks) | `rotations` only on a Literal motif, with angles from 0/90/180/270 and `allowRotationOnPlace` on; `maxPlacementAttempts` >= 1; `Fixed` needs a `fixedOrigin` inside the grid; a `fixedOrigin` with any other mode is an error (it would be ignored); `startOnExistingNode` needs `RandomValid` and no `minNodeDistance` > 0. |
| Curves | `straightLineBias` within 0-100; tension values within 0-0.5 with positive total weight; `explicitJitterDegrees` within 0-180; proximity-guard distances >= 0; `recheckProximityInCombined` needs the guard enabled; `forceAllEdgesCurved` needs `straightLineBias` 0 and the guard disabled. |
| Quality | `maxSimilarity` > 0; `compositeMaxSimilarity` > 0; `maxRegenerationAttempts` >= 1. |
| Rendering | Colours are hex (`#RRGGBB` / `#AARRGGBB`, `#RGB`/`#ARGB` also accepted); `contactSheet.columns` >= 1; `perGlyphJitter` within 0-0.9; widths within 0-1; `flareLength` within 0-0.5; `miterLimit` >= 1; `ballScale` and `taperSharpness` > 0; `pressureVariation` within 0-1; `edgeWobble`, `raggedness`, `startBlobScale` >= 0. |
| Enums | Every enum value is a defined one (guards against numbers like `42`). |
| `subSymbolSpacing`, `usageSizeBias`, `combination.bias` | Their own rules: finite non-negative distances, `max` >= `min`, valid modes and `lookback`, no opposite bias modes, bias weights only for listed modes and not all 0, `strength` >= 0. `connectionWeighting` must be a defined value. |

Every range also needs `min` <= `max`. Ranges that used to be clamped silently (tension, widths, jitter, percentages) are now errors, so what you configure is what you get.

What the loader cannot know is whether your constraints are satisfiable together (tight spacing on a small grid, a literal motif that doesn't fit its placement, and so on). Those surface as generation errors:

Generation-time errors you may meet:

- *"Sub-symbol i/n ... could not be placed"* (also for motifs): the message now breaks the attempts down into chains that ran out of moves, spacing rejections, and shapes that left the grid or crossed strokes, with a hint for the dominant cause. Loosen spacing or crossing rules, shorten the chains, or raise `maxPlacementAttempts`.
- *"Could not generate a sufficiently distinct, non-trivial glyph"*: the message lists, per attempt, why it was rejected ("too similar to 'warrior' (0.83)", or "a single straight line"). Raise `maxRegenerationAttempts` or `maxSimilarity`, or widen the count/length ranges.
- *"Could not satisfy combination recipe"*: allow scale/rotation, raise `maxRetriesPerRecipe`, relax spacing, or use a larger grid.
- *"Circular combination dependency"*: two recipes depend on each other's components.

---

## 15. Recipes and interactions

**Compact, connected glyphs.** Sub-symbols that cluster instead of scattering:

```json
"subSymbolSpacing": { "enabled": true, "maxNodeDistance": 3, "maxDistanceMode": "NearestShape" }
```

**Sub-symbols that branch from each other.**

```json
"allowSymbolsToCross": true,
"subSymbolPlacement": { "mode": "RandomValid", "startOnExistingNode": true, "maxPlacementAttempts": 30 }
```

**Airy strokes that don't fold back on themselves.**

```json
"subSymbolSpacing": {
  "enabled": true,
  "selfSpacing": [ { "mode": "Existing", "minDistance": 1.4 } ]
}
```

**Square-ish composites.**

```json
"combination": { "bias": { "modes": [ "Squarest" ] } }
```

**Angular script with flowing composites.**

```json
"curve": { "contextMode": "StraightMiniCurvedCombined" }
```

**Keep helper glyphs out of the atlas.** List them under `baseGlyphs`, reference them in `recipes`.

Interactions to keep in mind:

- Spacing applies to composites too: with `minCenterDistance` / `minNodeDistance` set, the plain (0,0) overlay of two components usually fails and components get pushed apart.
- `startOnExistingNode` and `minNodeDistance > 0` cannot coexist.
- `usageSizeBias` and `quality.maxSimilarity` both influence how varied plain glyphs are; strong low-size bias plus a strict similarity limit can make distinct glyphs harder to find.
- Literal motifs take their rotation angles from their own placement `rotations`, falling back to the `combination` rotation settings when it is not set; see the rotation note in section 6.
- With `allowSymbolsToCross: false`, no sub-symbol can touch or cross another, which makes dense glyphs on small grids hard to place.

---

## 16. Full example

A valid config using the current schema. Everything not shown takes its default.

```json
{
  "rootSeed": 20260927,
  "outputDirectory": "./output",
  "languages": [
    {
      "name": "Example",
      "grid": { "width": 15, "height": 15 },
      "canvas": { "sizePx": 512, "marginPx": 32 },

      "baseGlyphs": [ "shield_base" ],
      "glyphs": [ "warrior", "weapon", "protect", "fortress" ],

      "subSymbolCount": { "min": 1, "max": 3 },
      "subSymbolLength": { "min": 5, "max": 8 },
      "connectionRules": [
        { "direction": "Orthogonal", "minLength": 1, "maxLength": 4, "weight": 3.0 },
        { "direction": "Diagonal",   "minLength": 1, "maxLength": 3, "weight": 1.0 }
      ],
      "connectionWeighting": "PerRule",
      "allowNodeRevisit": false,
      "allowEdgeRevisit": false,
      "allowClosedLoop": true,
      "closedLoopWeight": 0.5,
      "allowSymbolsToCross": true,

      "guaranteedMotifs": [],

      "subSymbolPlacement": {
        "mode": "RandomValid",
        "fixedOrigin": null,
        "allowRotationOnPlace": true,
        "maxPlacementAttempts": 30,
        "startOnExistingNode": true
      },

      "subSymbolSpacing": {
        "enabled": true,
        "minCenterDistance": 1,
        "maxNodeDistance": 4,
        "maxDistanceMode": "NearestShape",
        "selfSpacing": [
          { "mode": "Existing", "minDistance": 1.4 },
          { "mode": "AllNodes", "maxDistance": 7 }
        ]
      },

      "usageSizeBias": { "strength": 0.5, "affectSubSymbolCount": true, "affectSubSymbolLength": true },

      "quality": { "maxSimilarity": 0.7, "rejectStraightLineOnly": true, "maxRegenerationAttempts": 10, "compositeMaxSimilarity": 0.95 },

      "combination": {
        "recipes": { "fortress": [ "warrior", "shield_base" ] },
        "allowScale": true,
        "allowedScales": [
          { "numerator": 1, "denominator": 1, "weight": 2.0 },
          { "numerator": 2, "denominator": 1, "weight": 1.0 }
        ],
        "allowRotate": true,
        "rotation": { "allowed": [ 0, 90, 180, 270 ], "weight": 1.0 },
        "maxRetriesPerRecipe": 5,
        "allowComponentsToCross": false,
        "bias": { "modes": [ "Squarest", "Shortest" ], "weights": { "Squarest": 2 }, "includeScaleAndRotation": false }
      },

      "curve": {
        "mode": "Derived",
        "contextMode": "AllCurved",
        "recheckProximityInCombined": true,
        "straightLineBias": { "min": 50, "max": 90 },
        "tension": { "options": [ { "min": 0, "max": 0, "weight": 3 }, { "min": 0.4, "max": 0.5, "weight": 1 } ] },
        "explicitJitterDegrees": { "min": 20, "max": 20 },
        "proximityGuard": { "enabled": true, "minEdgeLength": 2, "minDistanceToOtherNodes": 1 }
      },

      "render": {
        "framing": "Language",
        "foreground": "#FFFFFFFF",
        "background": "#00000000",
        "debugShowNodes": false,
        "debugColorComponents": false,
        "contactSheet": { "columns": 4, "baseGlyphsSheet": true },
        "typeface": {
          "kind": "Rectangular",
          "perGlyphJitter": 0.0,
          "rectangular": { "width": { "min": 0.045, "max": 0.045 }, "join": "Miter", "cap": "Butt", "miterLimit": 4.0 }
        }
      }
    }
  ]
}
```

---

## 17. Code prerequisites

Three options were added through one-line manual edits to config classes. Make sure they exist, otherwise the project will not compile (`StartOnExistingNode` is already present in your `PlacementConfig`):

```csharp
// CurveConfig
public CurveContextMode ContextMode { get; set; } = CurveContextMode.AllCurved;

// CombinationConfig
public CombinationBiasConfig Bias { get; set; } = new();

// PlacementConfig
public bool StartOnExistingNode { get; set; } = false;
```

The config layer also relies on these files in `GlyphGen.Core.Config`: `GridPointJsonConverter.cs`, `ConfigStructureChecker.cs`, `ConfigValidator.cs`, `JsonNames.cs` and `ConfigSchemaGenerator.cs`, next to the updated `GlyphGenJsonOptions.cs` (which registers the converter and runs both checkers). `GlyphUsageTracker` now lives in its own file in `GlyphGen.Core.Generation`.

---

## 18. Schema, samples and tests

### JSON Schema

`config.schema.json` describes the whole config for editors: autocomplete of option names, enum values and defaults, hover text for every option, and red squiggles for typos (unknown properties) and wrongly-typed values. It is generated from the config classes, so it never has to be written by hand:

```
GlyphGen.Cli --schema config.schema.json
```

Point a config at it with a `$schema` key (the loader ignores it); VS Code and most JSON editors pick it up:

```json
{
  "$schema": "./config.schema.json",
  "rootSeed": 1
}
```

- Hover text comes from the XML documentation the compiler writes next to the Core assembly. Turn it on with `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in the Core project (add `<NoWarn>$(NoWarn);CS1591</NoWarn>` to silence the missing-comment warnings). Without it the schema is identical minus the hover text.
- The schema covers structure, types, enum values, defaults and a few obvious minimums. Rules that relate one option to another (for example `forceAllEdgesCurved` needing `straightLineBias` at 0) are enforced by the loader, not the schema.
- Regenerate it whenever an option is added or changed. A test fails when the checked-in file has drifted.

### Samples

- `sample-config.json`: the two-language basic sample (Warrior and Devotee).
- `sample-config-advanced.json`: one language using most of the newer options (self-spacing rules, `startOnExistingNode`, a literal motif with its own `rotations`, `connectionWeighting`, composite similarity and crossing, weighted bias, `recheckProximityInCombined`, the `ThickEnds` typeface, contact-sheet settings). It generated cleanly across 40 different root seeds.

### Tests

`tests/GlyphGen.Tests` is a dependency-free test project (no xUnit or NUnit packages, so it restores anywhere):

```
dotnet run --project tests/GlyphGen.Tests              # everything
dotnet run --project tests/GlyphGen.Tests -- Spacing   # only tests whose name contains "Spacing"
dotnet run --project tests/GlyphGen.Tests -- --list    # list the tests
```

It exits with 0 when everything passes and 1 otherwise, so it works in CI unchanged. Adjust the two project paths in its `.csproj` to match your solution. The tests cover: `GridEdge.Intersects` (touching, T-junctions, collinear cases, symmetry), spacing between shapes and the three self-spacing modes (checked as invariants over hundreds of generated chains), combination bias and weights, crossing between components, retry variation, weighted fallback order, placement (`startOnExistingNode`, fixed origins, rotations), the curve plan mapped through a component's scale/rotation/translation, the context modes, the guard re-check, the quality gate and its messages, usage bias, connection weighting, the config loader (about sixty accept/reject cases), the schema, and reproducibility.

**Snapshots.** `SampleConfigSnapshot` and `AdvancedSampleSnapshot` compare the exact generated glyphs with `tests/GlyphGen.Tests/Snapshots/*.txt`. The first run records them (and says so); later runs fail if the output changes. After an intended change, re-run with `UPDATE_SNAPSHOTS=1`. They depend on your own seed code, so record them on your machine and commit them: that is what protects every existing seed from now on.
