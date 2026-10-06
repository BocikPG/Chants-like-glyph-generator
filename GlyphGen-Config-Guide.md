# GlyphGen configuration guide

GlyphGen builds a set of glyphs ("a language") from a JSON config: it grows random strokes on a lattice under your constraints, combines glyphs into composites, and renders everything to PNG.

```
GlyphGen.Cli <path-to-config.json>
GlyphGen.Cli --schema [config.schema.json]
GlyphGen.Cli --migrate old.json [new.json]
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
4. [Shapes](#4-shapes)
5. [Chain rules](#5-chain-rules)
6. [Placement](#6-placement)
7. [Arrangement](#7-arrangement)
8. [Size bias by usage](#8-size-bias-by-usage)
9. [Quality gate](#9-quality-gate)
10. [Combination (composites)](#10-combination-composites)
11. [Curves](#11-curves)
12. [Rendering](#12-rendering)
13. [Output files](#13-output-files)
14. [Config validation](#14-config-validation)
15. [Recipes and interactions](#15-recipes-and-interactions)
16. [Full example](#16-full-example)
17. [Migrating from the earlier layout](#17-migrating-from-the-earlier-layout)

---

## 1. How a run works

For each language, in order:

1. **Base glyphs** (`baseGlyphs`) are generated.
2. **Plain glyphs**: every entry of `glyphs` that is *not* a recipe key.
3. **Composites**: every entry of `glyphs` that *is* a recipe key, built by overlaying their components.

A **plain glyph** is built from the language's **`shapes`**, placed in list order (a guaranteed shape is one without a `count`; free-form strokes have a `count`), each validated against everything already placed using its own crossing, spacing and bias rules (section 7).

Each plain glyph then passes the **quality gate** (similarity to earlier glyphs, straight-line rejection); on failure it is regenerated with a fresh derived seed. Composites are exempt from the gate.

A **composite** is made by placing each component (translated, and optionally scaled/rotated) so that components don't cross each other and satisfy the spacing and bias rules. If no placement exists after all options, the least-used component is regenerated and the recipe is retried.

**Rendering** then plans curves for every glyph, frames them, builds stroke outlines and writes PNGs (section 13).

---

## 2. Root config

| Option | Type | Default | Notes |
|---|---|---|---|
| `rootSeed` | integer (ulong) | `0` | The only source of randomness; everything is derived from it. |
| `outputDirectory` | string | `"./output"` | Created if missing. |
| `languages` | array of language objects | `[]` | Each language is generated and rendered independently. |

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

**Glyph names** are plain strings (`"warrior"`). 

**Base glyphs**

- Generated first, so recipes can reference them.
- Rendered like everything else (same language-wide framing, same debug images), but written to `<language>/_bases/` and **left out of `_contact-sheet.png`**.
- Pass through the same quality gate as any other glyph.
- Must not also appear in `glyphs`, and must not be recipe keys.

---

## 4. Shapes

A plain glyph is built from the language's **`shapes`**: a list of entries processed **in order**, each placed against everything the earlier ones left in the glyph. A shape entry says *what* is drawn (`kind`, plus `chain` or `nodes`), *how many* (`count`), *where* it goes (`placement`) and *how it relates to the others* (`arrangement`). Every entry carries its own copy of these blocks; there are no language-wide defaults to inherit.

```json
"shapes": [
  {
    "id": "crossbar",
    "kind": "Literal",
    "nodes": [ [0, 0], [2, 0], [2, 2] ],
    "placement": { "mode": "RandomValid", "allowRotationOnPlace": true, "maxPlacementAttempts": 20 },
    "arrangement": { "allowCrossing": false }
  },
  {
    "id": "strokes",
    "count": { "min": 1, "max": 3 },
    "chain": {
      "length": { "min": 6, "max": 8 },
      "connectionRules": [
        { "direction": "Orthogonal", "minLength": 1, "maxLength": 5, "weight": 3.0 },
        { "direction": "Diagonal",   "minLength": 1, "maxLength": 5, "weight": 1.0 }
      ],
      "allowClosedLoop": true,
      "closedLoopWeight": 0.3
    },
    "arrangement": { "allowCrossing": true }
  }
]
```

| Field | Default | Notes |
|---|---|---|
| `id` | (required) | Unique within the language. Used in error messages and to seed the shape's random streams, so **renaming a shape changes the glyphs**. |
| `kind` | `Generative` | `Generative`: grown by the chain engine from `chain`. `Literal`: the fixed polyline in `nodes`. |
| `count` | omitted | `{min,max}`, inclusive, rolled once per glyph. **Omit it for a guaranteed shape**: exactly one instance, always present, no roll. |
| `chain` | see section 5 | Generative shapes only. |
| `nodes` | none | Literal shapes only: integer `[x, y]` pairs relative to the placement origin, joined in order as a polyline. Rotated about `(0,0)` if rotation applies, then translated to the origin. At least 2 points. |
| `placement` | see section 6 | Where the shape goes. |
| `arrangement` | see section 7 | How it relates to the other shapes. |
| `seedLabel` | none | Advanced: names the shape's random streams (default: `shape-<id>`). Only needed to make a migrated config reproduce its old glyphs; leave it out in new configs. |

**Guaranteed motifs and free-form strokes are the same thing.** A guaranteed motif is a shape without `count`; free-form strokes are shapes with one. Differences that follow from that: a shape **without** `count` is drawn blue in debug renders and is **not** affected by `usageSizeBias` (section 8); a shape **with** `count` is drawn in the free-stroke colour and is affected. A `count` range may include `0` (the shape is then sometimes absent). You can have any number of both kinds, e.g. long strokes and short ticks with different rules.

**Order matters.** Shapes are placed in list order and each is checked against everything already there, so put the most constrained shapes first (a `Fixed` or `CenterAnchor` motif, then the loose strokes). Order also affects spacing and crossing, see section 7.

Each instance is a separate placement. If one cannot be placed within its attempt budget, generation fails with an error naming the shape (and, for counted shapes, which instance).

A language with plain or base glyphs needs at least one shape. A language whose glyphs are all composites needs none.

---

## 5. Chain rules

`chain` describes how **one stroke** of a Generative shape grows: a self-avoiding walk of nodes on the grid, one move at a time. If no valid move exists, the chain ends early and that attempt fails; placement then retries (section 6). `chain` is ignored for Literal shapes.

```json
"chain": {
  "length": { "min": 6, "max": 8 },
  "connectionRules": [
    { "direction": "Orthogonal", "minLength": 1, "maxLength": 5, "weight": 3.0 },
    { "direction": "Diagonal",   "minLength": 1, "maxLength": 5, "weight": 1.0 }
  ],
  "connectionWeighting": "PerMove",
  "allowNodeRevisit": true,
  "allowEdgeRevisit": true,
  "allowClosedLoop": false,
  "closedLoopWeight": 0.0,
  "selfSpacing": [ { "mode": "Adjacent", "minDistance": 1.5, "lookback": 2 } ]
}
```

| Option | Default | Notes |
|---|---|---|
| `length` `{min,max}` | `3..6` | **Node count per chain, including the start node.** Minimum 2. |
| `connectionRules[]` | `[]` | The allowed moves, see below. At least one is needed to grow a chain. |
| `connectionWeighting` | `PerMove` | How rule weights become move probabilities, see below. |
| `allowNodeRevisit` | `false` | A chain may step onto a node it already visited. |
| `allowEdgeRevisit` | `false` | A chain may exactly retrace an earlier edge. When allowed, a retrace skips the geometry check. |
| `allowClosedLoop` | `false` | Allows a move back to the chain's start node, ending the chain (needs at least 3 nodes). A closed chain counts as complete even if shorter than the target. |
| `closedLoopWeight` | `0.0` | Weight of the loop-closing move against the other candidate moves. |
| `selfSpacing[]` | `[]` | Node-to-node spacing inside the chain, see below. |

`count` and `length` are integer ranges, **inclusive at both ends** (`1..3` can give 1, 2 or 3), chosen uniformly with the glyph's seeded random generator. If `max` is smaller than `min`, the result is simply `min` (the loader rejects that). Percent and float ranges used elsewhere (`straightLineBias`, widths, angles, jitter) are continuous and uniform between `min` and `max`.

### Connection rules

| Field | Notes |
|---|---|
| `direction` | `Orthogonal` (4 directions, the default), `Diagonal` (4 diagonals) or `Both`. |
| `minLength`, `maxLength` | Step length in grid cells along the direction. Both default to `1` (adjacent); minimum effective value 1. A diagonal step of length *n* spans *n* cells on each axis. |
| `weight` | Relative probability of choosing this rule (default `1.0`). A chain with a single rule uses it every time. |

At each step every geometrically valid concrete move (every direction x length of every rule) is listed, and one is picked by weight. With **`PerMove`** (default) each concrete move inherits its rule's full weight, so a rule with a wide length range or `Both` directions gets more total probability than a narrow rule with the same `weight`. With **`PerRule`** a rule's weight is split evenly over its valid moves at that step, so each rule's share depends only on the weights (a rule with no valid move drops out).

A chain **never crosses itself**, whatever `arrangement.allowCrossing` says; that flag controls crossing *other* shapes.

### Within one chain: `selfSpacing`

A list of rules applied by the chain generator **as each node is added**, so they steer growth instead of rejecting finished chains. Several rules (even of the same mode) can be active; a new node must satisfy all of them. They apply to Generative shapes only, and are active whenever listed (there is no master switch).

```json
"selfSpacing": [
  { "mode": "Adjacent", "minDistance": 1.5, "lookback": 2 },
  { "mode": "AllNodes", "maxDistance": 6 }
]
```

Distances are in grid units (Euclidean), `0` means "off".

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
- Large minimums cause more dead ends (shorter chains, more retries); raise `placement.maxPlacementAttempts` or the connection-rule length ranges if needed.
- A maximum smaller than your shortest connection step stops chains from growing (a diagonal step of length *n* is *n*·sqrt(2) long).
- `Existing` and `AllNodes` with the same minimum overlap: `AllNodes` is the stricter. Combining them is useful only with different values.

---

## 6. Placement

Each shape has its own `placement` block.

```json
"placement": {
  "mode": "RandomValid",
  "fixedOrigin": null,
  "allowRotationOnPlace": true,
  "rotations": null,
  "maxPlacementAttempts": 20,
  "startOnExistingNode": false
}
```

| Option | Default | Notes |
|---|---|---|
| `mode` | `RandomValid` | `RandomValid`: random grid point per attempt. `Fixed`: always `fixedOrigin`, which must then be set and on the grid. `CenterAnchor`: exactly the grid center (`width / 2`, `height / 2`, integer division). Every mode is still collision- and spacing-checked. |
| `fixedOrigin` | `null` | A grid point, required for `Fixed` and an error with any other mode. Written as `{ "x": 7, "y": 7 }` or `[7, 7]`. |
| `allowRotationOnPlace` | `true` | Allows a random rotation per attempt. See the rotation note below. |
| `rotations` | `null` | Literal shapes only. `null`: use `combination.rotation.allowed` when `combination.allowRotate` is on (the original coupling). A list: use exactly these angles. `[]`: never rotate. Only 0, 90, 180, 270. |
| `maxPlacementAttempts` | `20` | Attempts per instance (>= 1). Only used when something varies between attempts (random position, rotation, or a generated shape); otherwise one attempt. Generative shapes always vary. |
| `startOnExistingNode` | `false` | New shapes start on a node already in the glyph. See below. |

Each attempt resolves an origin, asks the shape to build itself there, then checks grid bounds, crossing and spacing. The first attempt that passes wins (with a bias active, up to `arrangement.candidates` such placements are collected, see section 7).

**Rotation note.** Rotation changes only **Literal shapes**: a generated chain ignores the rotation value (it is already random), so for Generative shapes `allowRotationOnPlace` has no visible effect (and the loader rejects `rotations` there).

### `startOnExistingNode`

Instead of a random grid point, the origin is drawn from the nodes already committed in the glyph (earlier shapes included), so shapes grow out of each other. The first shape, with nothing to attach to, is placed normally.

Requirements (the loader rejects violations):

- `mode` must be `RandomValid`.
- `arrangement.spacing.minNodeDistance` must be `0` when that shape's spacing is enabled (a shared node is at distance 0, so every attempt would be rejected).

Edges that only meet at a shared node do **not** count as crossing (section 7), so this works with crossing off as well.

---

## 7. Arrangement

`arrangement` says how a shape relates to the shapes **already placed** in the glyph. It is the same block on every entry of `shapes` and, as `combination.arrangement`, for the components of a composite.

```json
"arrangement": {
  "allowCrossing": false,
  "spacing": { "enabled": true, "minCenterDistance": 1, "maxNodeDistance": 4 },
  "bias": { "modes": [ "Tallest" ] },
  "candidates": 3
}
```

### Crossing

`allowCrossing` (default `false`). If `false`, none of this shape's edges may cross an already-placed edge. Edges that merely **meet at a shared node** (end to end, or a loop closing on its start) never count as crossing. A true crossing, a node landing on another edge's interior (a T-junction) and a collinear overlap beyond a single point all do. Passing over a node does not occupy it.

**A crossing is allowed only if both shapes allow it:** the shape being placed *and* the shape that owns the edge it would cross. So a shape with `allowCrossing: false` can never be crossed, not even by a shape that is itself allowed to cross. That lets a motif protect itself:

```json
{ "id": "bar",     "kind": "Literal", "nodes": [[0,0],[4,0]], "arrangement": { "allowCrossing": false } },
{ "id": "scribble", "count": {"min": 2, "max": 3}, "chain": { ... }, "arrangement": { "allowCrossing": true } }
```

Here the scribbles may cross each other but never the bar. Because shapes are placed in order, this also means a later shape that forbids crossing cannot cross *anything*, including earlier shapes that allow it.

### Spacing

Block: `arrangement.spacing`. Disabled by default. Distances are in grid units (Euclidean), `0` means "off". Nothing here overrides a hard constraint; failing candidates are simply discarded and retried.

A "shape" here is one placed instance of a shapes entry, and in composites a whole placed component (all its chains pooled). The spacing rules of the shape **being placed** are checked against **every** shape already placed, so the result depends on placement order: the later shape's rules decide.

| Option | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Master switch for this block. |
| `minCenterDistance` | `0` | Minimum distance between the centroids (average node position) of two shapes. |
| `minNodeDistance` | `0` | Minimum distance between the **closest pair of nodes** of two different shapes. |
| `maxNodeDistance` | `0` | Maximum distance between the closest node pair of the candidate and an existing shape. Must be >= `minNodeDistance` when both are set. |
| `maxDistanceMode` | `NearestShape` | `NearestShape`: within range of **at least one** existing shape (keeps the glyph connected). `AllShapes`: within range of **every** existing shape. |

The first shape in a glyph has nothing to be near, so the maximum doesn't apply to it. (Spacing *within* one chain is `chain.selfSpacing`, section 5.)

### Bias

`bias` is the same block as for composites (section 10: `modes`, optional `weights`; `includeScaleAndRotation` is for composites only and is rejected on a shape). It is a **ranking only**: it chooses among placements that already pass every hard rule (grid bounds, crossing, spacing) and never relaxes one.

For a shape it works as **best of N**: the shape is placed up to `candidates` times (default `3`, minimum 1), each a fully valid placement on its own random stream, and the one whose **glyph bounding box** (everything already placed plus the candidate) scores best is kept. Ties keep the earliest. Consequences:

- With no `bias.modes`, or `candidates: 1`, nothing changes: the first legal placement is used and the output is identical to a config without bias.
- A shape that cannot vary (a `Fixed`, non-rotating Literal) yields identical candidates and just uses one.
- More candidates means a stronger effect and more work per shape.
- `candidates` is ignored for composite components (they search every placement exhaustively).

---

## 8. Size bias by usage

Block: `usageSizeBias`. Counteracts a size skew towards heavily-used component glyphs: the more recipes a plain glyph appears in, the likelier it rolls **low** values for its shape counts and chain lengths.

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
| `affectSubSymbolCount` | `true` | Bias each counted shape's `count`. |
| `affectSubSymbolLength` | `true` | Bias each counted shape's `chain.length`. |

How it works: "uses" = how many times the glyph's name appears across `combination.recipes`. A glyph with *u* uses rolls its count/length `1 + strength × u` times and keeps the **lowest** result (a fractional part is applied as a probability). The result always stays inside the configured `min..max`.

Rough feel for a 3-value range and a glyph used twice: `strength: 0.5` gives the lowest value ~55% of the time (33% without bias); `strength: 1` gives ~70%.

Not affected: glyphs used in no recipe, **shapes without a `count`** (guaranteed shapes have fixed rules), and which component gets regenerated.

**Why it exists.** When a composite can't be placed, the least-used component is regenerated. Rarely-used glyphs are therefore re-rolled until they fit, which favours smaller shapes, while heavily-used glyphs stay at their original, larger size.

Turning this on changes the random draws of biased glyphs, so those glyphs will look different from before.

---

## 9. Quality gate

Block: `quality`. Applied to every plain glyph (base glyphs included) against every glyph accepted earlier in the language. A failing glyph is regenerated with a fresh derived seed up to `maxRegenerationAttempts` times, then the run fails with an error naming the glyph. **Composites are exempt**: they contain their components by construction.

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
| `maxRegenerationAttempts` | `10` | Attempts per glyph. |

Similarity is the Jaccard index of the two glyphs' node sets after shifting each so its bounding box starts at the origin. It is **not** rotation-, mirror- or scale-invariant: the same shape rotated or mirrored is not flagged.

---

## 10. Combination (composites)

Block: `combination`. A composite is a glyph whose name is a key in `recipes`; it is built by overlaying its component glyphs.

```json
"combination": {
  "recipes": {
    "fortress": [ "warrior", "protect" ],
    "citadel":  { "components": [ "warrior", "protect" ], "arrangement": { "allowCrossing": true } }
  },
  "allowScale": true,
  "allowedScales": [
    { "numerator": 1, "denominator": 2, "weight": 1.0 },
    { "numerator": 2, "denominator": 1, "weight": 1.0 }
  ],
  "allowRotate": true,
  "rotation": { "allowed": [ 0, 90, 180, 270 ], "weight": 1.0 },
  "maxRetriesPerRecipe": 5,
  "arrangement": {
    "spacing": { "enabled": true, "minNodeDistance": 1 },
    "bias": { "modes": [ "Shortest", "Narrowest" ], "includeScaleAndRotation": false }
  }
}
```

| Option | Notes |
|---|---|
| `recipes` | Map of composite name to a recipe: a **list** of component names (plain glyphs or base glyphs), or an **object** `{ "components": [...], "arrangement": {...} }` with a per-recipe arrangement. Both forms can be mixed. Single-level only for generation order: a composite built from another composite must be listed after it in `glyphs`. |
| `allowScale` | Default `false`. Allow scaled placements as a fallback. Does nothing while `allowedScales` is empty. |
| `allowedScales[]` | Default empty. Each item is `{ numerator, denominator, weight }`, defaulting to `1`, `1`, `1.0`. Shrinking (`< 1`) is only used when every node coordinate scales to a whole number (lattice-safe); enlarging always is. A scale's `weight`, times `rotation.weight` for a non-zero angle, sets how early that combination is tried (0 = never). Keep numerator and denominator positive. |
| `allowRotate` | Default `false`. Allow rotated placements as a fallback. Does nothing while `rotation.allowed` is just `[0]`. |
| `rotation.allowed` | Default `[0]`. Allowed rotation angles in degrees. **Only `0`, `90`, `180` and `270` are supported**; the loader rejects anything else. |
| `maxRetriesPerRecipe` | Default `5`. How many attempts (each followed by regenerating a dependency) before the recipe fails. |
| `arrangement` | How the components relate to each other: crossing, spacing, bias (below). Same block as a shape's arrangement. |

### Arrangement for components

`combination.arrangement` applies to every recipe; a recipe in the object form can override parts of it. **Whatever the recipe's `arrangement` lists (`allowCrossing`, `spacing`, `bias`) replaces that key of the combination's; keys it leaves out are inherited.** `spacing` and `bias` are replaced as whole blocks, not merged field by field.

- **Crossing**: default `false`. Overlay composition is controlled stacking: components don't cross each other unless the arrangement allows it. They may always touch at shared nodes. This is independent of any shape's `allowCrossing` (which governs strokes inside one glyph).
- **Spacing**: each placed component counts as **one** shape, checked against the components already placed in this composite. With `minCenterDistance` / `minNodeDistance` set, the plain (0,0) overlay of two components usually fails and components get pushed apart.
- **Bias**: below. `candidates` does not apply: components search every placement exhaustively.

### Placement search

For each component, in order:

1. **Identity first**: scale 1/1, rotation 0, every translation that keeps the shape on the grid, shuffled; the (0,0) overlay is tried before the others.
2. If nothing fits, every allowed **scale x rotation** combination (shuffled, or weight-ordered) is tried, each with its full translation search.
3. Only when everything is exhausted is a dependency regenerated: the component used by the fewest composites built so far goes first (ties follow recipe order), cycling through the recipe's components on later attempts. The regenerated glyph replaces the canonical one, and every other recipe using it is rebuilt to match.

Every candidate must satisfy: all nodes inside the grid, no crossing with already-placed components (unless allowed), and the arrangement's spacing rules.

### Bias

Steers composites towards an overall shape. It is a **ranking only**: it chooses among placements that already pass every constraint, never relaxes one, and if only one placement is legal it is used.

| Option | Default | Notes |
|---|---|---|
| `bias.modes` | `[]` | Empty = off (first legal placement is used). Any of `Tallest`, `Shortest`, `Widest`, `Narrowest`, `Squarest`. |
| `bias.weights` | `{}` | Optional weight per listed mode (default 1), e.g. `{ "Tallest": 3, "Squarest": 1 }`. Only the ratios matter. |
| `bias.includeScaleAndRotation` | `false` | Composites only. `false`: bias picks only among translations, keeping the translate-first order. `true`: every allowed scale and rotation competes too, so `Tallest` could choose a larger scale or a 90-degree turn. |

- "Size" is the bounding box of the combined node positions, in grid units.
- Several modes can be combined; each contributes a score relative to the grid size and the scores are added (times their weights). Examples: `Shortest` + `Narrowest` = smallest box; `Tallest` + `Widest` = biggest; `Tallest` + `Narrowest` = tall and thin; `Squarest` + `Shortest` = compact and square.
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
- Reused curves are not re-checked against neighbouring components, so a bulge can reach into an adjacent component. `CurvedMiniStraightCombined` avoids that.
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
| `debugShowNodes` | Draws the faint lattice with coordinate labels every 5 cells, plus node dots: red = free-form sub-symbol, blue = motif, **gold = a node used more than once** (a revisit or a node shared by shapes). |
| `debugColorComponents` | Fills each combination component in its own colour. |
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

The CLI always writes an extra `.debug.png` per glyph with node dots and component colours on, whatever these two debug flags say.

---

## 13. Output files

```
<outputDirectory>/
  <Language>/
    warrior.png              plain glyphs and composites
    warrior.debug.png        same glyph with nodes + component colours
    fortress.png
    _contact-sheet.png       6 columns, real glyphs only
    _bases/
      you_base.png           base glyphs (only if baseGlyphs is used)
      you_base.debug.png
```

- File and folder names are sanitized (characters invalid in file names become `_`).
- The contact sheet lists real glyphs in the order of `glyphs`, each cell at the glyph's own pixel size with a light-grey frame and 8 px gaps, no labels. The column count (6) is fixed in `Program.cs`. If `render.background` is transparent or translucent, the sheet uses white so glyphs stay visible.
- The console also prints an ASCII preview of every glyph (straight lines only), marking base glyphs.
- PNGs can differ slightly between platforms and Skia versions because of anti-aliasing; geometry is deterministic, but don't expect byte-identical files.

---

## 14. Config validation

`RootConfigLoader` works in three steps and reports **all** problems of the failing step at once (`Invalid config: - ...`):

1. **Structure** (on the raw JSON): unknown properties (with a "did you mean" hint), unexpected `null`s, the per-language `grid`, and **options from the old layout** (each error names where the option lives now, and points at `--migrate`).
2. **Recipe overrides** are merged into `combination.arrangement` (section 10).
3. **Values**:

| Area | Rule |
|---|---|
| Grid | No top-level `grid`; every language has its own; width and height >= 1. |
| Glyph names | Non-empty; unique across `glyphs` and `baseGlyphs`. |
| Recipes | A base glyph can't be a recipe key; a recipe needs components; every component must be listed in `glyphs` or `baseGlyphs`; a composite built from a composite must come after it in `glyphs` (which also rules out cycles). |
| `shapes` | At least one when there are plain/base glyphs. Each `id` non-empty and unique; `seedLabel` unique. `count` >= 0 with `max` >= `min`. Literal: `nodes` present, >= 2 points, no repeated consecutive point. Generative: no `nodes`; `chain.length` >= 2; at least one valid `connectionRules` entry (valid direction, `minLength` >= 1, `maxLength` >= `minLength`, weights >= 0 and not all 0); `closedLoopWeight` >= 0; each `selfSpacing` rule valid. |
| `placement` | Known `mode`; `Fixed` needs a `fixedOrigin` on the grid, other modes must not have one; `maxPlacementAttempts` >= 1; `rotations` only on Literal shapes, with `allowRotationOnPlace` on, using only 0/90/180/270; `startOnExistingNode` only with `RandomValid` and not with `arrangement.spacing.minNodeDistance` > 0. |
| `arrangement` | Spacing distances finite and >= 0, `maxNodeDistance` >= `minNodeDistance`, valid `maxDistanceMode`; known bias modes, no `Tallest`+`Shortest` or `Widest`+`Narrowest`, bias weights valid; `candidates` >= 1; `includeScaleAndRotation` not on a shape. Checked for every shape, for `combination.arrangement` and for each recipe override. |
| `combination` | `maxRetriesPerRecipe` >= 1; scale numerators/denominators positive; rotation angles 0/90/180/270. |
| `usageSizeBias` | `strength` is finite and >= 0. |

Generation-time errors you may meet:

- *"Shape 'x' (instance i/n) for glyph 'g' could not be placed"*: the message says which kind of failure dominated. Loosen `arrangement` (crossing, spacing), the chain rules, or raise `placement.maxPlacementAttempts`.
- *"Could not generate a sufficiently distinct, non-trivial glyph"*: raise `maxRegenerationAttempts`, raise `maxSimilarity`, or widen the `count` / `chain.length` ranges.
- *"Could not satisfy combination recipe"*: allow scale/rotation, raise `maxRetriesPerRecipe`, relax `combination.arrangement` (or that recipe's), or use a larger grid.
- *"Circular combination dependency"*: two recipes depend on each other's components.

---

## 15. Recipes and interactions

**Compact, connected glyphs.** Strokes that cluster instead of scattering:

```json
"arrangement": { "spacing": { "enabled": true, "maxNodeDistance": 3, "maxDistanceMode": "NearestShape" } }
```

**Strokes that branch from each other.**

```json
"placement": { "mode": "RandomValid", "startOnExistingNode": true, "maxPlacementAttempts": 30 },
"arrangement": { "allowCrossing": true }
```

**Airy strokes that don't fold back on themselves.**

```json
"chain": { "selfSpacing": [ { "mode": "Existing", "minDistance": 1.4 } ] }
```

**A motif nothing may cross, with free scribbles around it.** Give the motif `allowCrossing: false` and the scribbles `true` (section 7).

**Long strokes plus short ticks.** Two counted shapes with different `chain.length` and `connectionRules`.

**Square-ish glyphs.** `"arrangement": { "bias": { "modes": [ "Squarest" ] }, "candidates": 4 }` on a stroke shape; the same `bias` under `combination.arrangement` for composites.

**One recipe that may overlap.** `"fortress": { "components": [...], "arrangement": { "allowCrossing": true } }`.

**Angular script with flowing composites.**

```json
"curve": { "contextMode": "StraightMiniCurvedCombined" }
```

**Keep helper glyphs out of the atlas.** List them under `baseGlyphs`, reference them in `recipes`.

Interactions to keep in mind:

- Spacing applies to composites too, through `combination.arrangement`; it is separate from the spacing on shapes.
- Spacing is order-dependent: a shape's rules are checked against the shapes before it, so give the shape that should "stay close" the `maxNodeDistance`.
- `startOnExistingNode` and `minNodeDistance > 0` cannot coexist on the same shape.
- `usageSizeBias` and `quality.maxSimilarity` both influence how varied plain glyphs are; strong low-size bias plus a strict similarity limit can make distinct glyphs harder to find.
- The rotation settings live under `combination` but also feed placement of Literal shapes unless the shape lists its own `rotations`; see section 6.
- With `allowCrossing: false` on every shape, no stroke can cross another, which makes dense glyphs on small grids hard to place. Edges may still meet at shared nodes.
- A bias with many `candidates` on a tight grid multiplies the placement work per shape.

---

## 16. Full example

A valid config using the current schema. Everything not shown takes its default. `sample-config.json` and `sample-config-advanced.json` next to the CLI are complete, working examples.

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

      "shapes": [
        {
          "id": "bar",
          "kind": "Literal",
          "nodes": [ [0, 0], [3, 0] ],
          "placement": { "mode": "CenterAnchor" },
          "arrangement": { "allowCrossing": false }
        },
        {
          "id": "strokes",
          "count": { "min": 1, "max": 3 },
          "chain": {
            "length": { "min": 5, "max": 8 },
            "connectionRules": [
              { "direction": "Orthogonal", "minLength": 1, "maxLength": 4, "weight": 3.0 },
              { "direction": "Diagonal",   "minLength": 1, "maxLength": 3, "weight": 1.0 }
            ],
            "connectionWeighting": "PerRule",
            "allowClosedLoop": true,
            "closedLoopWeight": 0.5,
            "selfSpacing": [
              { "mode": "Existing", "minDistance": 1.4 },
              { "mode": "AllNodes", "maxDistance": 7 }
            ]
          },
          "placement": { "mode": "RandomValid", "maxPlacementAttempts": 30, "startOnExistingNode": true },
          "arrangement": {
            "allowCrossing": true,
            "spacing": { "enabled": true, "minCenterDistance": 1, "maxNodeDistance": 4, "maxDistanceMode": "NearestShape" },
            "bias": { "modes": [ "Squarest" ] },
            "candidates": 3
          }
        }
      ],

      "usageSizeBias": { "strength": 0.5, "affectSubSymbolCount": true, "affectSubSymbolLength": true },

      "quality": { "maxSimilarity": 0.7, "rejectStraightLineOnly": true, "maxRegenerationAttempts": 10 },

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
        "arrangement": {
          "bias": { "modes": [ "Squarest" ], "includeScaleAndRotation": false }
        }
      },

      "curve": {
        "mode": "Derived",
        "contextMode": "CurvedMiniStraightCombined",
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

## 17. Migrating from the earlier layout

Before the shapes layout, a language had language-wide options (`subSymbolCount`, `subSymbolLength`, `connectionRules`, `allowSymbolsToCross`, `guaranteedMotifs`, `subSymbolPlacement`, `subSymbolSpacing`, ...) and `combination.allowComponentsToCross` / `combination.bias`. This is a **clean break**: the loader rejects old keys with a message naming the new location. To convert a config:

```
GlyphGen.Cli --migrate old.json new.json      # or omit new.json to print to the console
```

The migrated config **generates the same glyphs** for the same seed. To make that possible the tool writes a `seedLabel` on each shape (the name of its old random stream); you can delete those once you no longer need identical output (the glyphs will change). Where the old layout did something implicitly, the tool makes it explicit.

| Old | New |
|---|---|
| `subSymbolCount` | `count` on the free-form shape (the tool names it `strokes`) |
| `subSymbolLength` | `chain.length` |
| `connectionRules`, `connectionWeighting`, `allowNodeRevisit`, `allowEdgeRevisit`, `allowClosedLoop`, `closedLoopWeight` | the same names under `chain`, **copied onto every generative shape** (guaranteed generative motifs used to inherit the revisit flags and weighting silently) |
| `guaranteedMotifs[]` | entries of `shapes` without a `count`. `type` becomes `kind`; `literal.nodes` becomes `nodes`; `generative.lengthRange` becomes `chain.length`; the rest moves under `chain` |
| `subSymbolPlacement`, a motif's `placement` | `placement` on the shape |
| `allowSymbolsToCross` | `arrangement.allowCrossing` on every shape. Note it is now per shape and needs both shapes' permission |
| `subSymbolSpacing` (distances, `maxDistanceMode`, `enabled`) | `arrangement.spacing` on every shape, **and** `combination.arrangement.spacing` (it used to apply to components too) |
| `subSymbolSpacing.selfSpacing` | `chain.selfSpacing` on every generative shape; **dropped if `subSymbolSpacing.enabled` was false** (it was inactive then) |
| `combination.allowComponentsToCross` | `combination.arrangement.allowCrossing` |
| `combination.bias` | `combination.arrangement.bias` |
| `combination.recipes` lists | unchanged (the list form still loads) |
| `usageSizeBias` | unchanged; now affects shapes that have a `count` |
| `quality`, `curve`, `render`, `canvas`, `grid`, `combination.allowScale` etc. | unchanged |

Things that differ from the old behaviour once you edit a migrated config: crossing is decided per shape (both must allow it); spacing is the placing shape's own; a guaranteed motif's chain rules are its own (changing the language no longer changes it).
