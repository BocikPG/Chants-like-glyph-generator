# GlyphGen configuration guide

GlyphGen builds a set of glyphs ("a language") from a JSON config: it grows random strokes on a lattice under your constraints, combines glyphs into composites, and renders everything to PNG.

```
GlyphGen.Cli [config.json | folder | glob ...] [options]
GlyphGen.Cli --schema [config.schema.json]
```

**Command line.** Run with no arguments: the `configs` (or `config`) folder is used if it exists, else the GlyphGen configs in the current folder (a json file with a top-level `languages` list; `*.schema.json` is skipped). A folder argument means every config in it.

| Option | Meaning |
|---|---|
| *(no seed option)* | One run per config: its `run.seed` if the file sets one, else a random seed (printed, with the master seed it was drawn from). Configs without a seed share one random seed. |
| `--seed 7`, `--seed 1-20`, `--seed 3,7,11` | Run exactly these seeds, overriding `run.seed`. The same seeds for every config. |
| `--runs N` | N runs with random, distinct seeds, shared by every config. Cannot be combined with `--seed`. |
| `--master-seed X` | Draw the random seeds from X. A batch prints its master seed; `--master-seed X --runs N` repeats it. |
| `--threads N\|auto` | The thread budget. `auto` (default) is every core the process may use. |
| `--parallel-runs N` | Runs at once. Default: as many as the budget allows (never more than there are runs). |
| `--threads-per-run N` | Threads inside one run. Default: the budget divided by the runs at once. |
| `--out DIR` | Output to `DIR/<config>/<seed>/<language>/`. Default: the same layout under each config's `run.outputDirectory`. `<config>` is the file name without `.json` / `.language.json`. |
| `--no-render` | Generate only, write no images. Much faster when you only want to know how often a config succeeds. |
| `--fail-fast` | Start no new run after one failed (runs already going finish). |
| `--validate-only` | Load and check the configs, then stop. |
| `--summary-json FILE` | Write every run's config, seed, status, time and error to FILE. |

A single run prints the ASCII preview and every file written; a batch prints one line per run as it finishes and a summary per config (runs ok, min / median / max time, and each failing seed with the first line of its error). The exit code is 1 when any run failed. The output does not depend on the thread count: the same seed gives the same glyphs with 1 or 16 threads. A run's own `run.maxParallelism` is replaced by the command line's thread plan.

- Property names are **case-insensitive** (the guide uses camelCase).
- Enums are written as **strings** (`"Orthogonal"`, `"RandomValid"`, ...).
- Everything is **deterministic**: one `run.seed` drives the whole run. A glyph's seed depends only on the language name and the glyph name, so adding or removing a glyph does not change the others (unless it changes what they are compared with or combined with).

> **A note on sources.** Written from the source files, including every config class and the rendering code (layout, curve planning, typefaces, stroke building). A few small helpers (the model classes, `Vec2`, `SeedTree`) were not shown, but nothing here depends on them.

---

## Contents

1. [How a run works](#1-how-a-run-works)
2. [Root config](#2-root-config)
3. [Language: basics](#3-language-basics)
4. [Shapes](#4-shapes)
    - [Copy shapes](#copy-shapes-copyof)
5. [Chain rules](#5-chain-rules)
6. [Placement](#6-placement)
7. [Arrangement](#7-arrangement)
8. [Size bias by usage](#8-size-bias-by-usage)
9. [Quality gate](#9-quality-gate)
    - [Quality rules](#quality-rules-qualityrules)
10. [Combination (composites)](#10-combination-composites)
    - [Derived glyphs](#10b-derived-glyphs)
11. [Curves](#11-curves)
12. [Rendering](#12-rendering)
13. [Output files](#13-output-files)
14. [Config validation](#14-config-validation)
15. [Recipes and interactions](#15-recipes-and-interactions)
16. [Full example](#16-full-example)

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

Two top-level properties: `run` (everything about the run itself) and `languages`.

```json
{
  "run": { "seed": 20260927, "outputDirectory": "./output", "maxParallelism": 1, "contactSheet": { "columns": 6 } },
  "languages": [ ]
}
```

| Option | Type | Default | Notes |
|---|---|---|---|
| `run.seed` | integer (ulong) | `0` | The only source of randomness; everything is derived from it. |
| `run.outputDirectory` | string | `"./output"` | Created if missing. |
| `run.maxParallelism` | int | `1` | Threads generation and rendering may use: `1` = one thread (default), `0` = every core, `n` = at most n. **The result is identical whatever the number** (same seed, same glyphs); it only changes how fast they arrive. |
| `run.contactSheet.columns` | int | `6` | Columns of each language's `_contact-sheet.png` (>= 1). |
| `run.contactSheet.baseGlyphsSheet` | bool | `true` | Also write a sheet of the base glyphs. |
| `languages` | array of language objects | `[]` | Each language is generated and rendered independently. |

---

## 3. Language: basics

```json
{
  "name": "Warrior",
  "grid": { "width": 15, "height": 15 },
  "render": { "canvas": { "sizePx": 512, "marginPx": 32 } },
  "baseGlyphs": [ "you_base" ],
  "glyphs": [ "warrior", "weapon", "fortress" ]
}
```

| Option | Type | Default | Notes |
|---|---|---|---|
| `name` | string | `""` | Used for seeding and for the output folder name. |
| `grid.width`, `grid.height` | integer | 25 / 25 | **Required per language** in the JSON; each must be >= 1. Nodes sit on integer coordinates `0..width-1`, `0..height-1`. |
| `render.canvas.sizePx` | integer | `512` | Output image is square; minimum 16. |
| `render.canvas.marginPx` | integer | `32` | Space around the drawing (stroke thickness is added on top). |
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
    "placement": { "start": { "mode": "RandomValid" }, "rotation": { "allowed": true }, "retries": { "placement": 20 } },
    "arrangement": { "crossing": { "mode": "None" } }
  },
  {
    "id": "strokes",
    "count": { "min": 1, "max": 3 },
    "chain": {
      "length": { "min": 6, "max": 8 },
      "connectionRules": [
        { "direction": "Orthogonal", "minLength": 1, "maxLength": 5, "weight": 3.0, "backToStartChance": 10 },
        { "direction": "Diagonal",   "minLength": 1, "maxLength": 5, "weight": 1.0 }
      ],
      "allowClosedLoop": true
    },
    "arrangement": { "crossing": { "mode": "Anywhere" } }
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
| `chance` | `100` | Chance (0-100) that the shape is drawn in a glyph at all, rolled once per glyph and covering every instance (a `count` still applies when it is drawn). `100` always draws. A copy shape (below) whose original was skipped is skipped too. |
| `requires` | none | Draw this shape only when other shapes already exist (or don't) in the glyph. See *Requiring other shapes* below. |
| `isDrawn` | `true` | `false` makes the shape an invisible **blockade**: it is generated, placed, copied, derived and combined like any other shape, and it blocks other shapes (crossing, spacing, `startOnExistingNode`, nodes) - but it is left out of the rendered glyph, and out of the glyph's bounds for framing. Use it to keep an area free or to force strokes to bend around something. It shows only in the extra `.debug.png` images, in translucent orange, when `render.debugShowUndrawn` is on. The glyph's quality checks (similarity, straight-line) still see it. |
| `onlyForGlyphs` | all glyphs | List of glyph names (from `glyphs`/`baseGlyphs`): the shape is drawn **only** in these plain glyphs. Omitted = every plain glyph. A copy (`copyOf`) is drawn only where its original is, so a copy may narrow further but cannot add glyphs (listing one where the original is not drawn is an error). |
| `excludeGlyphs` | none | Glyph names this shape is never drawn in, applied after `onlyForGlyphs`. A shape left out of a glyph takes no random draws, so that glyph's other strokes and all unfiltered glyphs come out exactly as before. Errors: an unknown name, a composite (recipe) or derived glyph (not built from `shapes`; a derivation's `Add` step picks its own), or a plain glyph left with no shape at all. |
| `chainFrom` | none | Id of another Generative shape whose whole `chain` block (length, `connectionRules`, weighting, `selfSpacing`, loops, revisits) this shape uses in place of one of its own. Everything else stays the shape's own: `count`, `placement` (bounds, node masks, side pull, retries), `arrangement` (crossing, spacing, bias), `onlyForGlyphs` / `excludeGlyphs`, `chance`. It draws from its own random stream, so it does not repeat the source's strokes. The source can be anywhere in the list and may itself use `chainFrom`; loops, missing ids, Literal shapes and copies are reported. Leave `chain` out on such a shape (it would be ignored). Use it to draw the same kind of stroke in two places with different rules - e.g. a second stroke that must start on the first, with the first stroke's connection rules. |
| `seedLabel` | none | Advanced: names the shape's random streams (default: `shape-<id>`). Only needed to make a migrated config reproduce its old glyphs; leave it out in new configs. |

**Guaranteed motifs and free-form strokes are the same thing.** A guaranteed motif is a shape without `count`; free-form strokes are shapes with one. Differences that follow from that: a shape **without** `count` is drawn blue in debug renders and is **not** affected by `usageSizeBias` (section 8); a shape **with** `count` is drawn in the free-stroke colour and is affected. A `count` range may include `0` (the shape is then sometimes absent). You can have any number of both kinds, e.g. long strokes and short ticks with different rules.

**Order matters.** Shapes are placed in list order and each is checked against everything already there, so put the most constrained shapes first (a `Fixed` or `CenterAnchor` motif, then the loose strokes). Order also affects spacing and crossing, see section 7.

Each instance is a separate placement. If one cannot be placed within its attempt budget, generation fails with an error naming the shape (and, for counted shapes, which instance).

A language with plain or base glyphs needs at least one shape. A language whose glyphs are all composites needs none.

### Requiring other shapes (`requires`)

`requires` makes a shape depend on which shapes were already drawn in the current glyph. It is rolled **in addition to** `chance`: both must pass.

```json
{ "id": "accent", "chance": 60,
  "requires": { "present": ["bigRound"], "absent": ["dot"], "matchPresent": "All", "matchAbsent": "All", "chance": 80 } }
```

| Option | Default | Meaning |
|---|---|---|
| `present` | none | Shapes that must exist. |
| `absent` | none | Shapes that must **not** exist. |
| `matchPresent` | `All` | `All` needs every shape in `present`; `Any` needs at least one. |
| `matchAbsent` | `All` | `All` needs every shape in `absent` to be missing (none may exist); `Any` needs at least one of them missing, so the shape is skipped only when all of them exist. |
| `chance` | `100` | Chance (0-100), rolled once the lists are satisfied. |

"Exists" means drawn in this glyph so far (at least one instance), so only shapes listed **before** this one can be named; naming itself, an unknown or a later shape is an error, as is an empty `requires` or a shape in both lists. With `usageSizeBias.affectChance`, `requires.chance` is bent the same way as `chance`. A shape skipped by `requires` draws no random numbers from the main stream, so existing seeds are unaffected.

### Copy shapes (`copyOf`)

A shape can be a **copy of an earlier shape** instead of being drawn: add one more entry to `shapes` and give it `copyOf`. For every instance of the original that the glyph got, the copy entry makes one flipped, moved copy of it. Copies are mirror images of strokes that are already there, so a whole language can share one kind of symmetry.

```json
"shapes": [
  { "id": "strokes", "count": { "min": 2, "max": 3 }, "chain": { ... } },
  { "id": "twin",
    "copyOf": {
      "shape": "strokes",
      "flip": "Auto", "auto": { "by": "LongestStraight" },
      "gap": { "min": 1, "max": 2 }, "shift": { "min": 0, "max": 1 },
      "anchor": "OriginStart", "preferNoCrossing": true
    },
    "arrangement": { "crossing": { "mode": "None" } } }
]
```

The original must be listed **before** the copy (shapes are processed in order, so the copies are made after all instances of the original). A copy can itself be copied (four-way symmetry: copy once `Horizontal`, copy that `Vertical`). The copy entry takes no `chain`, `nodes`, `placement` or `count`; its `arrangement` (`crossing`, `spacing`, `bias`) decides which positions are acceptable. An original that was not drawn (a `count` of 0) leaves nothing to copy.

| Option | Default | Meaning |
|---|---|---|
| `shape` | required | Id of the shape to copy. |
| `flip` | `Auto` | `None` (only move), `Horizontal` (mirror left-right), `Vertical` (top-bottom), `Rotate180`, `Diagonal`, `AntiDiagonal`, `Random` (draw from `flips`) or `Auto` (a rule, below). The flip happens around the original chain's **start node**. |
| `flips` | Horizontal, Vertical, Rotate180 | `Random` only: the flips to draw from. |
| `auto.by` | `LongestStraight` | `Auto` only. `LongestStraight`: finds the longest run of consecutive edges that continue in one straight horizontal, vertical or diagonal line, and flips **across** it (a horizontal run gives a left-right mirror, and so on). `CenterWeight`: compares the average of the original's nodes with the board's middle and mirrors along the axis where they differ most, pushing the copy toward the middle. Ties are broken at random. |
| `auto.axis` | `Perpendicular` | `LongestStraight` only: where the mirror line runs relative to the longest run. `Perpendicular` (the original behaviour) mirrors across the run, so a horizontal run gives a left-right mirror and the copy continues it backwards. `Parallel` mirrors along it, so a horizontal run gives a top-bottom mirror and the copy lies beside the run; give it a `gap` of 1 or more, or the copy lies on top of the run. `Random` draws one of the two for each copy. |
| `auto.shapes` | none | Ids of earlier shapes the `Auto` rule looks at (all their instances in the glyph) instead of only the chain being copied: the longest straight run among their strokes, or the average of all their nodes. A listed shape that was not drawn adds nothing; if none was drawn the chain being copied is used. |
| `auto.toward` | `Middle` | `CenterWeight` only: `Edge` pushes the copy away from the middle instead. |
| `gap` | 0 to 0 | Steps the copy is moved **apart** from the original, in the direction the flip throws it (or the direction `CenterWeight` aims). `0` = the copy starts on the anchor itself, so the two share that node; positive = apart; negative = pushed back over the original (they overlap or cross). Drawn from the range for each copy. |
| `shift` | 0 to 0 | Steps the copy is slid sideways (across the gap direction), up or down at random. |
| `anchor` | `OriginStart` | Where the copy's start node goes before gap and shift: the original's own start, `ExistingNode` (a node already on the glyph) or `ExistingEdge` (a lattice point on a line already there). Existing points are tried in random order. |
| `preferNoCrossing` | `true` | All gap/shift/anchor combinations are tried in random order and the first that crosses nothing is taken. If every one crosses, the least-crossing one is kept **only** when the copy's `arrangement.crossing` is true; otherwise generation fails like any shape that cannot be placed. With `false` the first position that obeys the hard rules is taken. |
| `moveGlyphToFit` | `true` | When no position keeps the copy on the grid but one would if the strokes already drawn sat elsewhere, the **whole glyph so far moves over** (copy included, by the smallest whole-step move) to where it fits. Positions that cross nothing are preferred. `false` fails instead. Not used by copies in a derivation step. |
| `retries.base` | `100` | First retry. If no position works for the copy, the base shape (the first shape in the chain of copies that is not itself a copy) is redrawn with fresh random numbers and the copy tried again, up to this many times. Everything placed after the base is rebuilt around the new draw. `0` turns it off. Only helps when the base is generated, not a literal. |
| `retries.glyph` | `100` | Second retry, after the base redraws are spent: the whole glyph is redrawn from a fresh seed, up to this many times, before generation fails with the error (which then says how many retries were spent). `0` turns it off. Neither retry applies to copies in a derivation step. Glyphs where no copy fails are drawn exactly as before. |
| `mirrorCurves` | `true` | The copy's curves are the original's curves flipped, so the two are exact mirror images. `false` gives the copy curves of its own. |
| `respectSelfSpacing` | `false` | Applies the **original's** `chain.selfSpacing` rules between the copy and the original, as if the copy were more nodes of the same stroke: every node of the copy is measured against every node of the chain it copies (a node the two share is exempt, except in `AllNodes` mode; `Existing` and `Adjacent` both mean "every node of the original" here). Positions that break a rule are skipped like any other rejected position; the error then counts them (`broke the original's chain.selfSpacing`). A copy of a copy uses the rules of the first original. The copy's own nodes need no check, a flip keeps their distances. An error if the original has no `selfSpacing` rule with a `minDistance` or `maxDistance`. |
| `selfSpacingIgnoresOriginal` | `false` | With `respectSelfSpacing`: for `Existing` and `AllNodes` rules the **original chain is left out** and the copy is measured against every other node already in the glyph instead, so it may sit right next to its original but still keeps the rules' distances from everything else (other instances of the original's shape count as everything else). `Adjacent` rules still compare with the original. With nothing else in the glyph those rules always pass. An error without `respectSelfSpacing`. |

If a copy cannot be placed the error lists the flip that was chosen, the anchor and gap/shift ranges, how many positions were tried, and how many were rejected for leaving the grid (and, when the glyph could be moved, how large the glyph and copy are together), breaking `arrangement.spacing`, or crossing existing strokes, followed by a hint for the most common cause.

Notes. The hard rules still apply: the copy must fit the grid (circles and dots too) and obey `arrangement.spacing`, and with `crossing.mode: None` it may not cross anything. Note a copy with `gap: 0` shares its start node with the original, so a `minNodeDistance` spacing rule would reject it. Under `bias`, up to `candidates` acceptable positions are compared and the best-scoring kept. In a derivation's `Add` step a copy shape must be listed after its original in the same step, and its curves are drawn fresh. Every random choice uses its own stream, so adding a copy shape does not change the glyphs of the other shapes.

---

## 5. Chain rules

`chain` describes how **one stroke** of a Generative shape grows: a self-avoiding walk of nodes on the grid, one move at a time. If no valid move exists, the chain ends early and that attempt fails; placement then retries (section 6). `chain` is ignored for Literal shapes.

```json
"chain": {
  "length": { "min": 6, "max": 8 },
  "connectionRules": [
    { "direction": "Orthogonal", "minLength": 1, "maxLength": 5, "weight": 3.0,
      "curve": { "chanceToBeCurved": 30 } },
    { "direction": "Diagonal",   "minLength": 1, "maxLength": 5, "weight": 1.0 }
  ],
  "connectionWeighting": "PerMove",
  "allowNodeRevisit": true,
  "allowEdgeRevisit": true,
  "allowClosedLoop": false,
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
| `allowClosedLoop` | `false` | Allows the chain to go back to its start node, ending it (needs at least 3 nodes). A closed chain counts as complete even if shorter than the target. How likely that is is set per rule, by `backToStartChance`. |
| `selfSpacing[]` | `[]` | Node-to-node spacing inside the chain, see below. |

`count` and `length` are integer ranges, **inclusive at both ends** (`1..3` can give 1, 2 or 3), chosen uniformly with the glyph's seeded random generator. If `max` is smaller than `min`, the result is simply `min` (the loader rejects that). Percent and float ranges used elsewhere (widths, angles, jitter) are continuous and uniform between `min` and `max`.

### Connection rules

| Field | Default | Notes |
|---|---|---|
| `direction` | `Orthogonal` | Which moves the rule may make, see the table below. |
| `minLength`, `maxLength` | `1`, `1` | Step length in grid cells. A diagonal step of length *n* spans *n* cells on each axis. See each direction for what "length" means. Minimum 1, except for `Circle` (0 is a dot); `Arc` needs at least 1. |
| `weight` | `1.0` | Relative probability of choosing this rule. A chain with a single rule uses it every time. |
| `minAngle`, `maxAngle` | `0`, `180` | Limits the **turn** at the current node (not for `Circle`), see "Angles". |
| `anglePreset` | `None` | `Straight`, `Gentle`, `Right` or `Sharp`: a ready-made `minAngle`/`maxAngle` pair. Replaces them (set one or the other). |
| `backToStartChance` | `0` | 0-100. Each time this rule is the one picked, this is the chance that the chain instead goes back to its start node and closes. Needs `chain.allowClosedLoop`, at least 3 nodes, and a closing edge that crosses nothing; otherwise the rule just makes its normal move. If the chain has no valid move left, the best chance among the rules is tried before giving up. Replaces `closedLoopWeight`. |
| `chainLengthCost` | `1` | How much of `chain.length` this rule uses up each time it is drawn. `1` is the old behaviour (one node). `2` makes a drawn move count double, so a chain of length 5 made only of such moves has 3 nodes: use it to let a circle or a long arc "take the place" of several strokes. `0` makes the rule free (it never counts towards the length; a chain is stopped at 64 nodes, and a dead end before the length is reached still fails the attempt). The start node always counts 1 and closing a loop (`backToStartChance`) costs nothing. The chain is complete once the used length reaches `chain.length`, so the last move can overshoot it. |
| `split` | none | Straight rules only. Present = the edges this rule draws are cut into pieces by extra nodes on them, as the `SplitEdges` derivation step does. See "Splitting edges" below. |
| `curve` | see section 11 | How the edges **this rule** makes are bent. |

| `direction` | What it does |
|---|---|
| `Orthogonal` | The 4 straight directions. |
| `Diagonal` | The 4 diagonals. |
| `Both` | All 8. |
| `Any` | **Any node on the grid, at any angle.** Length is the Chebyshev distance `max(|dx|, |dy|)`, so length 1 is exactly `Both` and longer lengths also reach the in-between nodes (3 right and 1 up, say). Still limited by `minLength`/`maxLength` and, if set, the angle. |
| `Angle` | Like `Any`, but requires an angle limit (`anglePreset` or `minAngle`/`maxAngle`); the validator rejects it without one. Any other direction accepts the same limits too, so `Both` + `Right` is a valid way to say "diagonal or straight, but only right-angle turns". |
| `Circle` | Draws a **circle instead of a line.** The circle's **diameter** runs from the current node to the node `length` steps away in one of the 8 directions, and the chain carries on from that far node. A diagonal circle of length *L* has diameter *L*·√2. `minLength` may be `0`: length 0 is a **dot** (a disc as thick as the stroke) on the current node, and the chain carries on from the same node. A circle must fit inside the grid, and it blocks like any other edge: it may not cut through other lines or circles, but may touch them at nodes, contain them, or be tangent. Angle limits do not apply. Circles are drawn as their own closed shape, so they do not take part in joins. |
| `Arc` | Draws **part of a circle.** The circle is set like a `Circle`'s: its diameter runs from the current node to the node `length` steps away (`minLength` at least 1), and the chain carries on from that far node. Only an arc of it is drawn, picked at random from the rule's `arc` settings (see "Arcs and outline nodes"). The drawn arc may not reach the far node (a quarter circle, say), in which case the next stroke starts from the far node anyway; a half circle whose ends are the two diameter ends always connects. Same blocking and grid rules as a circle, applied to the arc only. Angle limits do not apply. |

### Splitting edges (`split`)

```json
{ "direction": "Orthogonal", "minLength": 4, "maxLength": 8,
  "split": { "chance": 60, "parts": { "min": 2, "max": 3 }, "distribution": "Equal", "snap": "Exact" } }
```

A rule with a `split` block cuts the edge it draws into pieces by putting nodes on it. With the default mode `Pieces` the nodes are **real nodes of the chain**: `arrangement.spacing`, `start.onExistingNode`, node masks, `NodesOnly` crossings and later shapes all see them, so another stroke can grow out of the middle of an edge. They cost no `chain.length` (only the rule's `chainLengthCost` is charged, once per drawn edge). Not for `Circle` and `Arc` rules, which have `outlineNodes`.

| Option | Default | Notes |
|---|---|---|
| `mode` | `Pieces` | `Pieces`: the edge is replaced by several edges that meet at the new nodes (real nodes of the chain). `ExtraNodes`: the edge is **left as drawn** (one edge, one stroke, any curve is planned for the whole edge) and the nodes are only added on it, like the outline nodes of a circle: they count for spacing, `start.onExistingNode`, node masks and `NodesOnly` crossings, but the chain does not walk through them (so `allowNodeRevisit` and `selfSpacing` ignore them). With `snap` `Nearest` the nodes are the lattice points nearest the ideal places, so they may sit slightly beside the edge (within `maxDeviation`); the edge itself is not bent. |
| `chance` | `100` | Chance (0-100), rolled per edge, that the edge is cut at all. |
| `parts` | `2..2` | Into how many pieces an edge is cut, drawn per edge from the range (minimum 2: one new node). |
| `distribution` | `Equal` | `Equal`: as evenly as the lattice allows. `Random`: at random places. |
| `snap` | `Exact` | `Exact`: only lattice points the edge really passes over are used, so the edge keeps its shape (an edge that passes over none, such as a knight's-move vector, stays whole). `Nearest`: the ideal place moves to the nearest lattice point and the edge becomes a slightly bent path; each piece is checked for crossings on its own. |
| `maxDeviation` | `0.75` | `Nearest` only: a node whose lattice point is further than this from the edge is not used. |
| `minEdgeLength` | `2` | Only edges at least this long (lattice units) are cut. |

Details:

- A new node is never put on a node that already exists (the chain's own or another stroke's), so splitting never creates a junction by accident.
- With `Pieces`, the pieces are separate edges. If the rule also has `curve.chanceToBeCurved` above 0, each piece is bent on its own.
- With `Pieces`, later moves see the new nodes as chain nodes: with `allowNodeRevisit: false` the chain cannot come back onto them, and `selfSpacing` rules measure against them.
- The same nodes can also be added afterwards, to a finished glyph, with a `SplitEdges` derivation step (section 10b).

### Arcs and outline nodes

**`arc`** (direction `Arc`): which part of the circle is drawn. The arc starts at an angle of a preset, runs **clockwise on screen**, and so also ends on an angle of the preset. Angles are in degrees, 0 = right, 90 = down.

`arc.anchor` (default `None`) decides whether the arc has to touch the node it is drawn from. With `None` the arc starts at any angle of `angles`, so it can float on its circle and miss the current node (it may look as if it starts in a random place, even from a node the chain started on). `Node`: one end of the arc is the current node (it starts or ends there, either way round), with a sweep from `minSweep` to `maxSweep`; `angles` is not used. `Diameter`: a half circle joining the current node and the far node, bulging to either side; `minSweep`, `maxSweep` and `angles` are not used. Set it on the rule, so it applies to every arc that rule draws, including the first one of a shape that starts on an existing node. With `Node` and a sweep other than 180, the next stroke still starts from the far node, which the arc does not reach.

| Field | Default | Meaning |
|---|---|---|
| `angles` | `Cardinal` | The angle preset: `Cardinal` (0, 90, 180, 270), `Diagonal` (45, 135, 225, 315) or `Both` (all eight). |
| `minSweep`, `maxSweep` | `90`, `270` | Smallest and largest sweep in degrees, multiples of the preset's step (90, or 45 for `Both`), below 360. `180` = a half circle. Every (start angle, sweep) pair is a candidate, and a rule's weight is shared between them. |

```json
{ "direction": "Arc", "minLength": 2, "maxLength": 4, "arc": { "angles": "Cardinal", "minSweep": 180, "maxSweep": 180 } }
```

**A single dot as a blocker.** A `Circle` rule with `minLength` and `maxLength` 0 draws a dot at the current node, and the chain carries on from that same node. Since `chain.length` counts **nodes** (a step adds one), a shape that is exactly one dot needs `"length": { "min": 2, "max": 2 }`; with a longer length the chain runs out of moves (it may not revisit the node), and generation fails with "ran out of valid moves". Combine with `isDrawn: false`, `placement.mode: Fixed` with several `fixedOrigin` points and `discardUsedOrigins`, and `crossing.mode: Strict` to place invisible keep-out points that nothing may touch.

**`outlineNodes`** (direction `Circle` or `Arc`): extra nodes along the outline. Nodes are always **lattice points**, so a point of the outline between lattice points is handled by the settings below. The extra nodes count as nodes of the shape for `arrangement.spacing`, `startOnExistingNode` (new shapes can grow out of them) and `NodesOnly` crossings; the chain itself still walks through its own nodes only. They are lost when a glyph is derived from another.

| Field | Default | Meaning |
|---|---|---|
| `mode` | `None` | `None`; `AtAngles`: a node at each angle of the preset that the circle/arc covers; `EveryLatticePoint`: a node on every lattice point the outline passes within `tolerance` of. |
| `angles` | `Cardinal` | `AtAngles`: `Cardinal`, `Diagonal` or `Both`. |
| `snap` | `Within` | `AtAngles`: `Within`: the node goes on the nearest lattice point if that is within `tolerance`, otherwise that angle gets no node. `Nearest`: always on the nearest lattice point. `Exact`: only where the point is exactly on a lattice point. |
| `tolerance` | `0.3` | In lattice units, 0-0.5. A circle with a 4-wide diameter has its diagonal points at about ±1.41, which is 0.59 from the nearest lattice point: they only get a node with `Nearest`. |

```json
{ "direction": "Circle", "minLength": 2, "maxLength": 4, "outlineNodes": { "mode": "AtAngles", "angles": "Both", "snap": "Within", "tolerance": 0.45 } }
```

### Angles

The turn at a node is the angle between the edge you came along and the new one: **180 = carry straight on, 90 = right angle, 0 = fold back onto yourself** (the same convention as `render.joins`). The first edge of a chain, and the edge after a circle or dot, has nothing to turn from and is never limited.

| `anglePreset` | Turn |
|---|---|
| `Straight` | 170-180 |
| `Gentle` | 120-170 |
| `Right` | 80-100 |
| `Sharp` | 0-60 |

```json
{ "direction": "Angle", "minLength": 1, "maxLength": 4, "anglePreset": "Right", "weight": 2 },
{ "direction": "Any",   "minLength": 2, "maxLength": 3, "weight": 1 },
{ "direction": "Both",  "minLength": 1, "maxLength": 2, "minAngle": 120, "maxAngle": 180 },
{ "direction": "Circle", "minLength": 0, "maxLength": 2, "weight": 0.5 }
```

With `PerMove`, a rule with many possible moves (`Any` with a long range has dozens) carries far more total weight than a narrow one of the same `weight`; use `connectionWeighting: "PerRule"` when you want the weights to mean what they say.

At each step every geometrically valid concrete move (every direction x length of every rule) is listed, and one is picked by weight. With **`PerMove`** (default) each concrete move inherits its rule's full weight, so a rule with a wide length range or `Both` directions gets more total probability than a narrow rule with the same `weight`. With **`PerRule`** a rule's weight is split evenly over its valid moves at that step, so each rule's share depends only on the weights (a rule with no valid move drops out).

A chain **never crosses itself** (curves included), whatever `arrangement.crossing` says; that setting controls crossing *other* shapes.

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
- Large minimums cause more dead ends (shorter chains, more retries); raise `placement.retries.placement` or the connection-rule length ranges if needed.
- A maximum smaller than your shortest connection step stops chains from growing (a diagonal step of length *n* is *n*·sqrt(2) long).
- `Existing` and `AllNodes` with the same minimum overlap: `AllNodes` is the stricter. Combining them is useful only with different values.

---

## 6. Placement

Each shape has its own `placement` block, grouped by what it decides: `start` (where the shape starts), `area` (where it may be), `rotation` (how it may be turned), `retries` (what is tried when it does not fit) and `side` (a pull towards a side).

```json
"placement": {
  "start":    { "mode": "RandomValid", "origin": null, "discardUsed": false, "onExistingNode": null },
  "area":     { "bounds": null, "nodes": null },
  "rotation": { "allowed": true, "angles": null },
  "retries":  { "placement": 100, "shape": 100, "glyph": 100 },
  "side":     null
}
```

Everything is optional; a shape with no `placement` at all uses the defaults above.

### `placement.start`

| Option | Default | Notes |
|---|---|---|
| `mode` | `RandomValid` | `RandomValid`: random grid point per attempt. `Fixed`: always `origin`, which must then be set and on the grid. `CenterAnchor`: exactly the grid center (`width / 2`, `height / 2`, integer division). Every mode is still collision- and spacing-checked. |
| `origin` | `null` | A grid point, required for `Fixed` and an error with any other mode. Written as `{ "x": 7, "y": 7 }` or `[7, 7]`, **or as an array of points** (`[[1, 1], [5, 5], { "x": 7, "y": 2 }]`): one is picked at random for each glyph, and if that origin fails (crossing, spacing, off the grid) the others are tried in a shuffled order before the shape counts as unplaceable. Every point must be on the grid (and inside `area.bounds`). A single point draws no random numbers. |
| `discardUsed` | `false` | `Fixed` with several `origin` points: an origin that an earlier instance of this shape (in the same glyph) was placed at is not offered again, so a shape with `count` 4 and four origins fills each once. When all are used, the instance cannot be placed. |
| `onExistingNode` | none | Present = new instances start on a node already in the glyph. See below. |
| `onPartnerNode` | none | Present = start on a node of a partner glyph (`combination.partners`): `partner`, `shapes`, `chance`, `ifNone`. See *Drawing against partners*. |

### `placement.area`

| Option | Default | Notes |
|---|---|---|
| `bounds` | *(whole grid)* | A box smaller than the grid that this shape must stay inside, e.g. `{ "minX": 4, "minY": 4, "maxX": 10, "maxY": 10 }` (inclusive node coordinates). It acts as the edge of the grid for this shape: chain moves, start points (`RandomValid` picks inside the box, `CenterAnchor` uses its middle) and circles' whole outlines must fit. Other shapes are not limited by it. Must lie inside the grid and contain `start.origin` if there is one. |
| `nodes` | none | **Node mask**: which lattice nodes this shape may use, on top of `bounds`. Every node of the shape's strokes (circle nodes and dots too), and the node it starts on, must pass it; chain moves onto other nodes are simply not offered, and a random start is drawn from the usable nodes only. Not applied to `copyOf` shapes. `{ "allow": [ region, ... ], "forbid": [ region, ... ] }`: a node is usable when it is in at least one `allow` region (or there is no `allow`) and in no `forbid` region. See *Node masks* below. |

### `placement.rotation`

| Option | Default | Notes |
|---|---|---|
| `allowed` | `true` | Allows a random rotation per attempt. See the rotation note below. |
| `angles` | `null` | Literal shapes only. `null`: use `combination.rotation.allowed` when `combination.allowRotate` is on (the original coupling). A list: use exactly these angles. `[]`: never rotate. Only 0, 90, 180, 270. Rejected for generated shapes and unless `allowed` is on. |

### `placement.retries`

What is tried, in this order, when a shape does not fit. **Every retry count defaults to 100** (here and everywhere else retries appear), so a glyph is only reported as failed after a lot of honest effort; set a count lower when you would rather fail fast.

| Option | Default | Notes |
|---|---|---|
| `placement` | `100` | Attempts per instance (>= 1). Only used when something varies between attempts (random position, rotation, or a generated shape); otherwise one attempt. Generative shapes always vary. |
| `shape` | `100` | If an instance still cannot be placed after `placement` attempts: redraw **all instances of this shape** (new chains, new positions) up to this many times before giving up on this try of the glyph. Shapes placed earlier stay as they are. |
| `glyph` | `100` | After `shape` is spent: redraw the **whole glyph** from a fresh seed (every shape again) up to this many times before the failure is reported. Each redraw is deterministic, so the same seed gives the same glyph. |

### `placement.side`

Pull the shape towards a side or corner (`start.mode` `RandomValid` only). See "Side pull" below.

Each attempt resolves an origin, asks the shape to build itself there, then checks grid bounds, crossing and spacing. The first attempt that passes wins (with a bias active, up to `arrangement.candidates` such placements are collected, see section 7).

**Rotation note.** Rotation changes only **Literal shapes**: a generated chain ignores the rotation value (it is already random), so for Generative shapes `rotation.allowed` has no visible effect (and the loader rejects `rotation.angles` there).


### Node masks (`placement.area.nodes`)

```json
"placement": {
  "area": {
    "bounds": { "minX": 2, "minY": 2, "maxX": 22, "maxY": 20 },
    "nodes": {
      "allow":  [ { "pattern": { "x": { "mode": "Even" }, "y": { "step": 3, "offset": 1 } } } ],
      "forbid": [ { "box": { "minX": 0, "minY": 0, "maxX": 6, "maxY": 6 } },
                  { "points": [[12, 12], [13, 12]] } ]
    }
  }
}
```

A region sets **exactly one** of `points`, `box`, `pattern`, `nodesOfShapes`, `boundsOfShapes`, `nodesOfComponents` or `nodesOfGlyph`.

- **Direct nodes**: `points` (`[[x,y], ...]`). **Rectangles**: `box` (`{ minX, minY, maxX, maxY }`, inclusive). Both work in `allow` or `forbid`.
- **Patterns** split X and Y: `"pattern": { "x": AXIS, "y": AXIS }`; a node matches when its x AND its y match (an axis you leave out matches everything). `AXIS` is `{ "mode": "Odd" }`, `{ "mode": "Even" }` or `{ "mode": "Every", "step": 3, "offset": 1 }` (the default mode: every 3rd coordinate starting at the offset).
- **Node pool** (`nodesOfShapes`): the nodes of other shapes, as far as they are placed *when this shape is placed*. A shape can only use nodes of shapes listed **earlier** in `shapes` (or its own earlier instances, if it names itself); a later shape is an error. With the pool in `allow`, a stroke may only run on those nodes.
- **Box around shapes** (`boundsOfShapes`): `{ "shapes": [ids], "margin": 0 }`. In `allow`: stay inside the rectangle that encloses the nodes of those shapes (grown by `margin`); in `forbid`: keep out of it. Same ordering rule as the pool; an empty pool (nothing placed yet) allows nothing.
- **Nodes of another glyph** (`nodesOfGlyph`): `{ "glyph": "name", "offset": [dx, dy] }`: the nodes of that glyph, moved by `offset`. The glyph is generated first, even when it is listed later in `glyphs`, and a glyph that uses it is redrawn whenever the source is redrawn. `"glyph": "*"` means every earlier plain glyph. Cycles (A uses B, B uses A) are an error.
- **Nodes of components** (`nodesOfComponents`, in a combination placement's `nodes`, see section 10): the nodes of the components already placed in the composite.
- If the rules leave no usable node (an empty pool, a mask with no node inside the bounds), placement fails at once with that explanation, rather than after all attempts.
- Only **nodes** are tested. A circle's outline may pass over masked-out nodes; its centre-line nodes and the node it is attached to must be allowed, and `bounds` still limits the whole outline.
- Works together with `spacing.onlyShapes` / `excludeShapes` and `start.onExistingNode.shapes`: `onExistingNode` only starts on existing nodes that the mask allows.
- A tight mask makes chains run out of moves sooner: lower the chain `length`, allow more nodes, or raise `placement.retries.placement`.

### `placement.start.onExistingNode`

```json
"start": { "mode": "RandomValid", "onExistingNode": { "chance": 100, "shapes": ["frame"], "ignoreStartNode": false } }
```

Instead of a random grid point, the origin is drawn from the nodes already committed in the glyph (earlier shapes included), so shapes grow out of each other. The first shape, with nothing to attach to, is placed normally.

| Option | Default | Notes |
|---|---|---|
| `chance` | `100` | The chance (0-100) that an attempt starts on an existing node; otherwise that attempt starts on a random grid point. Rolled per attempt, so a shape can mix both. With a chance below 100, `onExistingNode` and `spacing.minNodeDistance > 0` can coexist (the attempts that start elsewhere satisfy it). |
| `shapes` | all shapes | A list of ids of **earlier** shapes; only their nodes count as places to start on (copies and instances included, by shape id). Default: every node already in the glyph. If none of the listed shapes was drawn in this glyph (a `chance` roll, `onlyForGlyphs`), there is nothing to attach to and the shape is placed at a random point, like the very first shape. Errors: an empty list, an unknown id, or a shape not listed before this one. |
| `ignoreStartNode` | `false` | Leaves the node the shape starts on out of this shape's spacing checks (centroid, minimum and maximum node distance), since that node is shared with an existing shape and so sits at distance 0 from it. Every other node is still measured. |

Requirements (the loader rejects violations):

- `start.mode` must be `RandomValid`.
- `arrangement.spacing.minNodeDistance` must be `0` when that shape's spacing is enabled and `chance` is 100, unless `ignoreStartNode` is on (a shared node is at distance 0, so every attempt would be rejected).

Edges that only meet at a shared node do **not** count as crossing (section 7), so this works with crossing off as well.

### Side pull (`placement.side`)

`side` pulls a shape towards a side or corner of the glyph. It only applies with `start.mode: RandomValid` (the other modes always use the same origin).

```json
"placement": { "side": { "mode": "Prefer", "sides": [ "Top", "Left" ] } }
"placement": { "side": { "mode": "SetRandom", "pick": "Corner" } }
```

| Option | Default | Notes |
|---|---|---|
| `mode` | `Prefer` | `Prefer` uses `sides`; `SetRandom` draws the sides, see below. |
| `sides` | `[]` | `Prefer` only: `Left`, `Right`, `Top`, `Bottom`; one or two (two = a corner). Left with Right, or Top with Bottom, cancel out and are rejected. `Center` alone pulls towards the middle of the glyph. |
| `pick` | `Any` | `SetRandom` only: what is drawn, see below. |
| `weight` | `1.0` | How strongly the pull counts against `arrangement.bias`, when both are on. `0` turns it off. |

**How it works.** It is a ranking, like `bias`: the shape is placed up to `arrangement.candidates` times (each a fully legal placement), and the one whose bounding box is nearest the chosen side(s) is kept. It never relaxes a rule, and **the pull is as strong as `candidates` is high**: with the default of 3 it is mild, with 20 or 30 the shape lands almost at the edge. Top is the smaller y (row 0 is the top of the image), and the middle of the shape's own bounding box is what is measured. If `arrangement.bias` is also active the two scores are added.

**`SetRandom`.** The sides are drawn at random, then used exactly like `Prefer` with those sides. `pick` decides what is drawn:

| `pick` | Draws |
|---|---|
| `Any` | One of `Left`, `Right`, `Top`, `Bottom`. |
| `Corner` | Two sides, one horizontal and one vertical: a random corner. |
| `Horizontal` | `Left` or `Right`. |
| `Vertical` | `Top` or `Bottom`. |

The draw is made **for each instance** of the shape (a shape with a `count` can put each of its strokes on a different side), and comes from the glyph's seeded random generator, so the same seed gives the same sides. Drawing never changes the other random choices, so adding a side pull to one shape leaves everything else in the glyph as it was.

---

## 7. Arrangement

`arrangement` says how a shape relates to the shapes **already placed** in the glyph. It is the same block on every entry of `shapes` and, as `combination.arrangement`, for the components of a composite.

```json
"arrangement": {
  "crossing": { "mode": "None" },
  "spacing": { "enabled": true, "minCenterDistance": 1, "maxNodeDistance": 4 },
  "bias": { "modes": [ "Tallest" ] },
  "candidates": 3
}
```

### Crossing

`crossing` decides where this shape's edges may cross edges that are already placed. **Curves count**: a curved edge is checked as the curve it is drawn as (curves are planned while the shape is placed, so what is checked is what you see). Edges that merely **meet at a shared node** (end to end, or a loop closing on its start) never count as crossing. A true crossing, a node landing on another edge's interior (a T-junction) and a collinear overlap beyond a single point all do. Passing over a node does not occupy it. Thickness is not considered: that is the typeface's business.

| Field | Default | Meaning |
|---|---|---|
| `mode` | `None` | `None`: no crossing - edges may only meet at **shared nodes** (a junction on a node is fine, but a stroke may not pass through another stroke's middle, or through a **dot**, a circle of diameter 0). `NodesOnly`: a crossing is accepted only where it lies within `nodeTolerance` of a node. `Anywhere`: crossing is free. `Strict`: **no contact at all** - no crossing, and not even a shared node or an edge ending on another; nothing of this shape may touch any other shape (a pair is judged by the stricter of the two, so a `Strict` shape also keeps later shapes off it). Own consecutive edges still meet at their nodes. Not allowed with `placement.start.onExistingNode`. `AnywhereButRespectStrict`: **for `combination.arrangement` (and a recipe's `arrangement`) only** - the components may cross anywhere, like `Anywhere`, except that strokes drawn by a shape whose own `crossing.mode` is `Strict` stay `Strict` inside the combined glyph: nothing of another component may touch them, and the shape's `spacing.minEdgeDistance` (if enabled) is kept too. Use it so a dot of one component is never covered by another component's strokes (give the dot shape `crossing.mode: Strict` and `spacing.minEdgeDistance`). Rejected on a shape. |
| `nodeTolerance` | `0.3` | `NodesOnly`: how far (lattice units) from a node a crossing may lie and still count as "at" it. So a stroke that passes *near* a node, though not exactly through it, is fine. |
| `nodesOfShapes` | *(all)* | `NodesOnly`: only the nodes of these shapes (ids) count. Missing/empty = every node of every shape, and the shape's own nodes too. With a list, the shape's own nodes count only if its own id is in it. In `combination.arrangement.crossing` (and a recipe's) the ids are still **shape ids** (`shapes[].id`, never glyph names like `numberBase`): a node counts when the shape that owns it is listed, whether it belongs to a component already placed or to the one being placed. A glyph name or unknown id is reported. |

```json
"arrangement": { "crossing": { "mode": "NodesOnly", "nodeTolerance": 0.4, "nodesOfShapes": ["frame"] } }
```

**A crossing is allowed only if both shapes accept it:** the shape being placed *and* the shape that owns the edge it would cross, each by its own `crossing` settings (a `NodesOnly` shape also checks the crossing against its own `nodesOfShapes`). So a shape with `mode: None` can never be crossed, not even by a shape that is `Anywhere`. That lets a motif protect itself:

```json
{ "id": "bar",     "kind": "Literal", "nodes": [[0,0],[4,0]], "arrangement": { "crossing": { "mode": "None" } } },
{ "id": "scribble", "count": {"min": 2, "max": 3}, "chain": { ... }, "arrangement": { "crossing": { "mode": "Anywhere" } } }
```

Here the scribbles may cross each other but never the bar. Because shapes are placed in order, this also means a later shape that forbids crossing cannot cross *anything*, including earlier shapes that allow it.

**Speed.** Edges of a rule with `chanceToBeCurved` above 0 are not pre-checked as straight lines while the chain grows (the bend is only known once the chain is complete); the finished chain is checked once with its real curves, and rejected as a whole if it crosses something it may not. Rules that are always straight are still checked step by step.

### Spacing

Block: `arrangement.spacing`. Disabled by default. Distances are in grid units (Euclidean), `0` means "off". Nothing here overrides a hard constraint; failing candidates are simply discarded and retried.

A "shape" here is one placed instance of a shapes entry, and in composites a whole placed component (all its chains pooled). The spacing rules of the shape **being placed** are checked against **every** shape already placed, so the result depends on placement order: the later shape's rules decide.

| Option | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Master switch for this block. |
| `minCenterDistance` | `0` | Minimum distance between the centroids (average node position) of two shapes. |
| `minNodeDistance` | `0` | Minimum distance between the **closest pair of nodes** of two different shapes. |
| `minEdgeDistance` | `0` | Minimum distance, in grid units, between the **paths** of this shape's strokes and every other stroke in the glyph, and between the shape's own strokes - measured along the real curves (arcs, circles and bent lines included), not between nodes. It keeps strokes from hugging each other: parallel lines one cell apart, an arc resting on a line, a hairpin turn. Dots keep the distance too. Where strokes meet on purpose (a shared node, an allowed crossing) the rule is waived within 1.5 x the distance of the meeting point, so a stroke has to leave a junction at roughly 40 degrees or more. Between two shapes the larger of their two values applies. Needs `enabled: true`; for shapes only (rejected on `combination.arrangement` and recipes). A good value is about the stroke width in grid units (2 for a thick brush on a 25-cell grid). Costs generation time and makes placements fail more often, so raise `maxPlacementAttempts` / `redoShape` with it. |
| `maxNodeDistance` | `0` | Maximum distance between the closest node pair of the candidate and an existing shape. Must be >= `minNodeDistance` when both are set. |
| `maxDistanceMode` | `NearestShape` | `NearestShape`: within range of **at least one** existing shape (keeps the glyph connected). `AllShapes`: within range of **every** existing shape. |
| `onlyShapes` | none | Shape ids. The spacing of this shape (`minCenterDistance`, `minNodeDistance`, `maxNodeDistance` and `minEdgeDistance`) is measured **only** against strokes drawn by these shapes; everything else is ignored by it. May name the shape itself (its own other instances, e.g. keep one dot away from other dots only). Not together with `excludeShapes`. Shapes only. |
| `excludeShapes` | none | Shape ids this shape's spacing ignores: it is measured against everything else. E.g. a `minEdgeDistance` that keeps a stroke away from everything except the `bottomLine`. Not together with `onlyShapes`. Shapes only. |

Notes on the shape lists. A shape's own chain is always checked against itself (the list only governs other shapes and other instances of the same id). The list works both ways round for `minEdgeDistance`: a pair keeps the larger of the two shapes' values, but a shape that ignores the other one asks for no clearance from it, so with `A` ignoring `B` and `B` asking 3, `B` still keeps 3 from `A` unless `B`'s own list leaves `A` out. Inside combined glyphs (`AnywhereButRespectStrict`) a shape's clearance is kept against everything, as before; the lists are not applied there.

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
  "affectCount": true,
  "affectLength": true
}
```

| Option | Default | Notes |
|---|---|---|
| `strength` | `0` | Extra draws added **per use**. `0` = off (generation is unchanged). Must be a finite number >= 0. |
| `affectCount` | `true` | Bias each counted shape's `count`. |
| `affectLength` | `true` | Bias each counted shape's `chain.length`. |
| `affectChance` | `false` | Bias each shape's `chance` and `requires.chance`. The chance roll is made `1 + n` times (n = the extra draws): with a **negative** `strength` the shape is drawn if **any** roll succeeds (a chance of *p* becomes 1-(1-*p*)^(1+n)), with a **positive** one only if **all** do. Use it to give a rarely drawn big shape (`chance` 10-15) to heavily-used glyphs far more often than to the rest. |

How it works: "uses" = how many times the glyph's name appears across `combination.recipes`. A glyph with *u* uses rolls its count/length `1 + strength × u` times and keeps the **lowest** result (a fractional part is applied as a probability). The result always stays inside the configured `min..max`.

Rough feel for a 3-value range and a glyph used twice: `strength: 0.5` gives the lowest value ~55% of the time (33% without bias); `strength: 1` gives ~70%.

Not affected: glyphs used in no recipe, **shapes without a `count`** (guaranteed shapes have fixed rules), and which component gets regenerated.

**Why it exists.** When a composite can't be placed, the least-used component is regenerated. Rarely-used glyphs are therefore re-rolled until they fit, which favours smaller shapes, while heavily-used glyphs stay at their original, larger size.

Turning this on changes the random draws of biased glyphs, so those glyphs will look different from before.

---

## 9. Quality gate

Block: `quality`. Applied to every plain glyph (base glyphs included) against every glyph accepted earlier in the language. A failing glyph is regenerated with a fresh derived seed up to `retries` times, then the run fails with an error naming the glyph. **Composites are exempt from these two checks** (they contain their components by construction); only `quality.rules`, below, can check them.

```json
"quality": {
  "similarity": { "max": 0.7, "compositeMax": 1.0, "basis": "Nodes", "comparePosition": false },
  "rejectStraightLineOnly": true,
  "retries": 100
}
```

| Option | Default | Notes |
|---|---|---|
| `similarity.max` | `0.7` | Jaccard similarity (0-1) at or above which two glyphs count as too alike. `1.0` or higher disables the check. |
| `similarity.compositeMax` | `1.0` | The same limit for composites, which are only compared with each other. `1.0` (default) = no check for composites. |
| `similarity.basis` | `Nodes` | What `similarity.max` compares. `Nodes`: which nodes two glyphs use - glyphs that touch the same nodes are alike however the nodes are joined. `Edges`: which strokes they draw (lines, circles, arcs and whether bent), so glyphs built on the same few nodes still differ when they join them differently. **Use `Edges` when the shapes are restricted to a small fixed set of nodes** (`placement.nodes`): with `Nodes`, every glyph on the same 6 points scores 1.00 against the others and generation fails with "too similar". |
| `similarity.comparePosition` | `false` | `false`: each glyph is shifted so its bounding box starts at the origin before comparing, so the same shape drawn elsewhere is alike. `true`: compared where they lie on the grid - for glyphs whose position means something (coordinates, constellations). Applies to both bases. |
| `rejectStraightLineOnly` | `true` | Reject a glyph whose whole geometry is one straight line. |
| `retries` | `100` | Attempts per glyph. |

Similarity is the Jaccard index of the two glyphs' node sets after shifting each so its bounding box starts at the origin. It is **not** rotation-, mirror- or scale-invariant: the same shape rotated or mirrored is not flagged.

### Quality rules (`quality.rules`)

Declarative checks of a finished glyph, plain or composite, for things the similarity gate cannot see: "a dot of one base must not sit next to a dot of the other", "no two arcs alike close together". Rules run after the similarity and straight-line checks, in order, on every candidate glyph. Default: none. Everything below is off unless a rule is listed, so existing seeds are unchanged.

A rule looks at every stroke its **subject** selects, and for each one searches the strokes its **against** selector selects among the rest of the glyph.

```json
"quality": {
  "acceptPenalty": 0,
  "rules": [
    {
      "id": "dotsApart",
      "appliesTo": "Composite",
      "subject": { "shapes": ["dotsOnly"], "kind": "Dot" },
      "against": { "shapes": ["dotsOnly"], "kind": "Dot" },
      "measure": "Count",
      "window": { "shape": "Square", "radius": 3 },
      "max": 0,
      "whenCountsDiffer": { "shape": "dotsOnly", "action": "Skip" },
      "onFail": "Reject",
      "redo": "Either"
    }
  ]
}
```

| Option | Default | Notes |
|---|---|---|
| `id` | required | Unique name, shown in errors and warnings. |
| `appliesTo` | `Both` | `Plain` (plain and base glyphs, derived ones included), `Composite`, or `Both`. |
| `onlyForGlyphs`, `excludeGlyphs` | all | Glyph names the rule applies to / skips. |
| `subject` | any stroke | The strokes that are judged: `shapes` (ids of the shapes that drew them; empty = any) and `kind` - `Any`, `Dot` (a circle of diameter 0 only), `Circle` (any whole circle, **dots included** - what the `Circle` connection rule draws), `Ring` (whole circles above diameter 0, no dots), `Arc` (part of a circle), `Line` (straight), `Curve` (bent line). A rule whose `subject` or `against` matches nothing in the whole run is reported as a warning at the end (a wrong `kind` or shape id is the usual cause). |
| `against` | any stroke | The strokes searched for, selected the same way. |
| `among` | `Auto` | Which strokes count as "something else": `Auto` = in a composite, strokes of the **other components**; in a plain glyph, strokes of other chains. `OtherComponents` (composites only) and `OtherChains` (any stroke outside the subject's own chain) say it outright. |
| `measure` | `Count` | `Count`: how many `against` strokes lie in the window; more than `max` breaks the rule. `Similar`: how many of those **look like the subject** (similarity at or above `similarityThreshold`); more than `max` breaks it. `Distance`: the nearest `against` stroke must be at least `minDistance` away (the window is not used). |
| `window` | `Square`, `4` | Count and Similar. `Square`: the subject's bounding box grown by `radius` on every side (a square around a dot). `Circle`: everything within `radius` of the subject's path. Lattice units. |
| `max` | `0` | Count and Similar: how many strokes are allowed. |
| `minDistance` | `0` | Distance: required, above 0. |
| `similarity` | `Visual` | Similar. `Visual`: both strokes are drawn with a brush `strokeWidth` wide, centred on each other, and the overlap (intersection over union) is the similarity - a dot next to a dot is alike, a dot next to a long line is not. `Structural`: same kind and about the same size (lines and curves: length within 1 cell; arcs: sweep within 45 degrees and diameter within 1 cell), giving 1 or 0. |
| `similarityThreshold` | `0.7` | Similarity at or above which two strokes count as alike. |
| `strokeWidth` | `1.5` | Visual similarity: brush width in lattice units. |
| `whenCountsDiffer` | off | Composites. `{ "shape": id, "action": "Skip" or "Loosen", "factor": 0.5 }`. The number of instances of that shape (its rolled `count`) is compared between the two components of a pair of strokes. When they differ - one base has 1 dot and the other 3, which tells them apart by itself - `Skip` leaves that pair out, and `Loosen` multiplies the window `radius` (and `minDistance`) of that pair by `factor`. |
| `onFail` | `Reject` | `Reject`: the glyph is thrown away and generated again (fresh seed, up to `quality.retries`), and the run fails with the rule's `id` and an example of what was found if no attempt passes. `Warn`: the glyph is kept; the problem is printed as a warning at the end of the run. `Score`: a soft rule - every stroke that breaks it adds `weight` to the glyph's **penalty**. A glyph whose total penalty is above `quality.acceptPenalty` (default 0) is tried again, and if no attempt gets under it, the attempt with the lowest penalty is kept (with a warning) instead of failing the run. |
| `weight` | `1` | Score only. |
| `redo` | `Composite` | Composites, `onFail: Reject` only. A retried composite is always arranged again, but when the problem is inside a component (the dots belong to the bases) that alone never changes anything. `Subject` also redraws the component that holds the judged stroke, `Other` the component that holds the stroke it clashed with, `Either` alternates between them. Only plain and base glyphs are redrawn (a composite or derived component is not), each redraw uses a new seed, and **every other composite already built from the redrawn glyph is rebuilt with it**, so a symbol stays the same in every composite. If one of those composites no longer fits or breaks a Reject rule, the redraw is undone. |

Notes. Rules are checked on the finished glyph, so they see every stroke, whatever order the shapes were placed in. A violation is counted per judged stroke, so `Score` weights add up per stroke. `Score` rules ignore `redo` (the best attempt kept at the end must still match the components it was built from). Rules cost generation time, roughly one pass over the glyph's strokes per rule; with a large `redo` cascade (many composites built from one base) expect the run to take noticeably longer. Rules that are not satisfiable show up as errors naming the rule, e.g. `rule 'dotsApart' broken (2x): dot of 'dotsOnly' in actionBase at (13,4) has 1 stroke(s) within 3 (dot of 'dotsOnly' in placeBase at (12,4)), at most 0`.

#### Rules between glyphs (`quality.pairRules`)

`quality.rules` look inside one glyph. `quality.pairRules` judge the strokes of one glyph against the strokes of **another** glyph, for glyphs that never appear together (two bases whose dots must not sit in the same place). They use the same fields as `quality.rules` (`measure`, `window`, `max`, `minDistance`, `similarity`, `onFail`, `weight`, `whenCountsDiffer`, ...), except that `subject` and `against` also take **`glyphs`** (required): the names of the glyphs they pick strokes from (`"*"` = any glyph). `appliesTo`, `among`, `onlyForGlyphs` and `excludeGlyphs` are not used.

```json
"pairRules": [
  {
    "id": "placeVsActionDots",
    "subject": { "glyphs": ["placeBase"],  "shapes": ["dotsOnly"], "kind": "Dot" },
    "against": { "glyphs": ["actionBase"], "shapes": ["dotsOnly"], "kind": "Dot" },
    "measure": "Distance", "minDistance": 4,
    "onFail": "Reject", "redo": "Either"
  }
]
```

- The rule is checked whenever a glyph named in it is accepted, against every glyph of the other side that already exists. A broken Reject rule retries the glyph being built; `redo` (`Subject`, `Other`, `Either`) also redraws the earlier glyph the stroke clashed with (plain/base glyphs only; every composite built from it is rebuilt, and the redraw is undone if that fails or breaks a Reject rule). `redo: Composite` just retries the new glyph.
- Strokes are compared where they lie on the grid, glyph on top of glyph, so `Distance` and `Count` mean "if the two glyphs were drawn on one another". To compare regardless of position use a window as large as the grid (`radius` 25 on a 25 grid) with `Similar`.
- `Similar` between dots is pointless (every dot looks like every dot, so the rule can never pass); for dots use `Distance`. `Similar` is for lines, curves and arcs.
- If both sides name the same glyphs with the same selector, each pair is judged once. Several glyphs per side (`["placeBase", "actionBase"]` on both) check every pair among them.
- `whenCountsDiffer` compares how many instances of the shape each of the two glyphs rolled.
- A rule that never finds a stroke on one side gives the same "never found" warning as `quality.rules`.
- Tight pair rules raise the redraw count; when bases are also tightly constrained elsewhere (very strict `secondStroke` shapes), the run can fail for another reason. Raise `maxRegenerationAttempts` / `maxPlacementAttempts` or loosen the distance.

---

## 10. Combination (composites)

Block: `combination`. A composite is a glyph whose name is a key in `recipes`; it is built by overlaying its component glyphs.

```json
"combination": {
  "recipes": {
    "fortress": [ "warrior", "protect" ],
    "citadel":  { "components": [ "warrior", "protect" ], "arrangement": { "crossing": { "mode": "Anywhere" } } }
  },
  "allowScale": true,
  "allowedScales": [
    { "numerator": 1, "denominator": 2, "weight": 1.0 },
    { "numerator": 2, "denominator": 1, "weight": 1.0 }
  ],
  "allowRotate": true,
  "rotation": { "allowed": [ 0, 90, 180, 270 ], "weight": 1.0 },
  "retries": 100,
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
| `retries` | Default `100`. How many attempts (each followed by regenerating a dependency) before the recipe fails. |
| `arrangement` | How the components relate to each other: crossing, spacing, bias (below). Same block as a shape's arrangement. |
| `partners` | Default none. Map of a glyph to the glyphs it is **drawn against** (see *Drawing against partners* below). |
| `partnerContact` | Default `true`. A glyph drawn against partners has to touch every one of them; a draw that misses one is rejected and drawn again. |

### Drawing against partners (`partners`)

Normally every glyph is drawn alone and a recipe then searches for a placement where the components fit, touch and don't collide. When they don't, a component is redrawn and the search starts again, which is where most of the time goes. `combination.partners` turns this around: a glyph is **drawn knowing the glyphs it will be combined with**, so it fits by construction.

```json
"combination": {
  "recipes":  { "Seek": ["verbBase", "seekBase"], "Find": ["verbBase", "findBase"] },
  "partners": { "seekBase": ["verbBase"], "findBase": ["verbBase"] }
}
...
{ "id": "specificStrokes", "onlyForGlyphs": ["seekBase", "findBase"],
  "placement": { "start": { "onPartnerNode": { "shapes": ["linker"], "ifNone": "Anywhere" } } } }
```

- The partners are drawn first (the order follows the dependencies, whatever the list order) and sit in the **same frame**. Their strokes are obstacles for every shape of the glyph, under `combination.arrangement.crossing` and `spacing.minEdgeDistance`, and under the shape's own `crossing`.
- A recipe that contains such a glyph is a **plain overlay**: every component stays where it was drawn, there is no placement search, no redrawing to make them fit, and `arrangement.placements`, `contact` and `bias` do not apply to it. It must list the glyph together with **all its partners**, and have **at least one component without partners** (a root, drawn on its own). With several roots (say `peopleBase` and `personBase` for `brotherBase`), each stays where it was drawn and the partner glyph is drawn against all of them, touching each; nothing arranges the roots relative to each other, so they may overlap. Use the placement search (leave the glyph out of `partners`) when the roots must be spaced or arranged.
- `placement.start.onPartnerNode` starts a shape on a node of a partner (`partner` names one, default every partner; `shapes` limits it to nodes of those partner shapes; `chance` per attempt). With several partners give one shape per partner. `ifNone` says what happens when there is no such node: `Anywhere` (default) starts at a random point, `Skip` leaves the shape out, `Fail` stops with an error. Not together with `onExistingNode`.
- With `partnerContact` (default) the finished glyph must share a node with, or cross, each partner; otherwise the draw is rejected like any other quality failure and drawn again (only that glyph, not its partners).
- **Redraws:** quality rules whose `redo` points at a partner redraw it, and every glyph drawn against it is drawn again (through the same quality checks), then the composites are rebuilt. A partner may itself be a derived glyph (for example a rotated one). A partner cycle is an error.
- `partners` keys are glyphs drawn from shapes (not composites or derived glyphs); a partner is any non-composite glyph.

### Arrangement for components

`combination.arrangement` applies to every recipe; a recipe in the object form can override parts of it. **Whatever the recipe's `arrangement` lists (`crossing`, `spacing`, `bias`) replaces that key of the combination's; keys it leaves out are inherited.** `spacing` and `bias` are replaced as whole blocks, not merged field by field.

- **Crossing**: default `false`. Overlay composition is controlled stacking: components don't cross each other unless the arrangement allows it. They may always touch at shared nodes. This is independent of any shape's `crossing` (which governs strokes inside one glyph).
- **Spacing**: each placed component counts as **one** shape, checked against the components already placed in this composite. With `minCenterDistance` / `minNodeDistance` set, the plain (0,0) overlay of two components usually fails and components get pushed apart. `minEdgeDistance` keeps the **paths** of different components apart (strokes that meet at a shared node or cross where crossing is allowed are waived near the meeting point), which is the setting to reach for when components lie on top of each other.
- **Contact**: `arrangement.contact` makes the components of a composite touch.

```json
"contact": { "chance": 100, "min": 1, "kind": "Either", "nodesOfShapes": ["frame", "linker"] }
```

  At least `min` contacts (default 1), counted over the whole composite, are needed, with `chance` (0-100, default 100) deciding per composite whether it is held to this. `kind` is `Junction` (a node two components share), `Crossing` (an edge cutting through another; needs `crossing.mode` `NodesOnly`, `Anywhere` or `AnywhereButRespectStrict`) or `Either` (default). `nodesOfShapes` (shape ids, not glyph names; default: any) limits what counts: a shared node counts when it is a node of a listed shape (in either component), a crossing when one of the two crossing edges belongs to a listed shape. If the components do not touch, the last placed one is moved to a position where they do, then the other movable ones are tried in turn. A component with a `Fixed` placement is not moved, and a recipe whose components (after the first) are all `Fixed` is exempt.
- **Bias**: below. `candidates` does not apply: components search every placement exhaustively.

### Placing individual components

`arrangement.placements` controls where a named component goes. It is a list of rules, each naming a component glyph; components without a rule are placed as described below. It lives in `combination.arrangement`, and a recipe's own `arrangement` can replace the whole list for that recipe.

```json
"arrangement": {
  "placements": [
    { "glyph": "frame", "mode": "Fixed", "offset": { "x": 0, "y": 0 } },
    { "glyph": "dot",   "mode": "Prefer", "sides": [ "Top", "Right" ] },
    { "glyph": "tick",  "sides": [ "Bottom" ] }
  ]
}
```

| Option | Default | Notes |
|---|---|---|
| `glyph` | (required) | The component's name, **or a list of names** (`["dot", "tick"]`) to give them all the same rule. It applies wherever that glyph is a component of a recipe the arrangement covers, and to every instance if a recipe lists it twice. Use `"*"` to apply the rule to **every component** that has no rule of its own naming it (a rule naming a glyph always wins over `"*"`). With `SetRandom` each component still draws for itself, and with `Fixed` they all get the same offset (so they would overlap, unless crossing allows it). Only one `"*"` rule per list. |
| `mode` | `Prefer` | `Prefer`, `SetRandom` or `Fixed`, see below. |
| `sides` | `[]` | `Prefer` only: `Left`, `Right`, `Top`, `Bottom`; one or two. Or `Center` alone: a soft pull towards the middle of the grid (not a hard rule, and not combinable with other sides). |
| `nodes` | none | `Prefer` and `SetRandom`: a **node mask** the component's nodes must hit. Same regions as a shape's `placement.area.nodes` (`points`, `box`, `pattern`) plus `nodesOfComponents`: `{ "components": [glyph names], "shapes": [shape ids] }`, the nodes of components placed earlier (optionally only those of some shapes inside them). Only components placed before this one exist, so put the others earlier in the recipe or give them a `Fixed` placement. |
| `anchor` | `FirstThenAny` | With `nodes`: which node of the component has to be on the mask. `FirstThenAny`: its very first node, and only if no placement is found that way, any node of it. `FirstNode`: the first node, no fallback. `AnyNode`: any node from the start. |
| `pick` | `Any` | `SetRandom` only: `Any`, `Corner`, `Horizontal` or `Vertical`. |
| `scope` | `Language` | `SetRandom` only: `Language` or `Composite`, see below. |
| `offset` | none | `Fixed` only: `{ "x": 2, "y": 0 }` or `[2, 0]`. |
| `ifBlocked` | `Regenerate` | `Fixed` only: what happens when the fixed position is blocked (crossing, spacing, off the grid). `Regenerate`: the recipe attempt fails and a component is redrawn. `Translate`: a component's place in a composite **always equals its place in its own glyph**, so a blocked component is not just slid inside the composite: (1) it is tried at another free position, shuffled order; (2) the **original glyph is moved to that position too**; (3) every other finished composite that uses the glyph is rebuilt with the moved original (one attempt each, nothing regenerated or moved); (4) if one of them can't be rebuilt, the next position is tried; (5) if no position works, the attempt fails and a component is redrawn as usual. Only drawn glyphs are moved (not composites); glyphs derived from a moved glyph are not moved with it. Moving happens only when the composite is built in its normal turn, not inside another composite's redraw cascade. |
| `weight` | `1.0` | `Prefer` and `SetRandom`: how strongly the pull counts against `bias` when both are on. `0` turns it off. |

**`Fixed`.** The component goes exactly where it is in its own glyph, moved by `offset` (omitted = not moved: the plain overlay). It is never scaled or rotated and has no alternatives: if it leaves the grid, crosses something or breaks the spacing, the recipe cannot be built and fails with an error. Regenerating the other components doesn't help a fixed one, so the others are placed around it (fixed components are placed first automatically).

**`Prefer`.** A hard rule, compared by the **middles** (bounding-box centres) of the components: a component with `sides: ["Left"]` always ends up with its middle strictly left of the middle of every other component in the composite, `Right` strictly right, `Top` above, `Bottom` below. Two sides mean a corner (`["Top", "Left"]` is above **and** left of all the others). Left with Right, and Top with Bottom, cancel out and are rejected. Top is the smaller y, because row 0 is the top of the image. The rule also binds the components placed around it: an unruled component can't end up on the wrong side of a ruled one. Among the placements that satisfy this and every other hard rule (grid bounds, crossing, spacing), the one nearest the chosen side is used (the same ranking as before, added to `bias` if that is on). If two components ask for the same side, the one placed first is the more extreme.

**Order.** Components with a rule are placed before the others (`Fixed` first, then side rules; recipe order breaks ties), so they get the pick of the grid and the rest are placed around them. The finished composite still lists its components in recipe order. Because the side rule is hard, conflicting rules, or too little room on a small grid, can make a recipe unbuildable; it then fails with an error that says so.

**`SetRandom`.** The sides are drawn at random, then used exactly like `Prefer` with those sides. `pick` decides what is drawn:

| `pick` | Draws |
|---|---|
| `Any` | One of `Left`, `Right`, `Top`, `Bottom`. |
| `Corner` | Two sides, one horizontal and one vertical: a random corner. |
| `Horizontal` | `Left` or `Right`. |
| `Vertical` | `Top` or `Bottom`. |

`scope` decides how often the draw is made:

- **`Language`** (default): once for the whole language, per glyph. The glyph gets the same side in **every** composite it is part of (Man/Human always in the same corner), and the draw is fixed by the root seed, so quality retries of a composite never move it.
- **`Composite`**: once per composite, so the same glyph can sit on different sides in different composites, and on a different one after a quality retry.

Note what a side means: the pull is toward that side of the **grid** (the nearest legal placement is used), and the hard rule is that the component's middle ends up on that side of the other components' middles. Drawing never changes the other random choices.

---

## 10b. Derived glyphs

Block: `derivations` (language level), a map from glyph name to a recipe for editing **another glyph**. The source is generated first, then the steps edit a copy of its strokes; the source itself is left as it was.

```json
"derivations": {
  "Cemetery": {
    "from": "death_base",
    "maxSimilarityToSource": 0.85,
    "steps": [
      { "op": "DeleteNodes", "mode": "Bridge", "target": "Inner",
        "amount": { "percent": { "min": 10, "max": 30 } },
        "connectionRules": [ { "direction": "Any", "minLength": 1, "maxLength": 3 } ],
        "keepConnected": true },
      { "op": "DeleteEdges", "amount": { "count": { "min": 1, "max": 2 } }, "removeOrphanNodes": true },
      { "op": "Add", "shapes": ["tick"], "optional": true },
      { "op": "Scale", "factors": [ { "numerator": 2, "denominator": 3 } ] }
    ]
  }
}
```

- The name must be listed in `glyphs` or `baseGlyphs` and must not also have a recipe. It is generated **after** its source wherever the two are listed; the source may be a plain, base, composite or itself derived glyph (a composite is flattened into plain strokes). Circular dependencies are rejected.
- A derived glyph can be a component of a recipe, or the source of another derivation.
- `finalOutputRotation` (default `FollowMoves`) decides whether the derived glyph is drawn at its source's `render.transform.rotateOptions` angle instead of its own. Only steps that actually ran count (one skipped by `chance` or `onlyIf` does not): `Own` never follows; `FollowMoves` follows when only `Scale`, `Mirror`, `Rotate` or `Nudge` ran; `FollowRemovals` also follows after `DeleteNodes`, `DeleteEdges` and `Keep` (what is left is a part of the source); `FollowAlways` follows whatever ran, `Add` included.
- Everything random comes from the glyph's seed and the step's position, so the same seed gives the same result; a quality retry draws a different edit.
- **Curves are kept.** An edge that survives untouched (not moved, and not new) keeps the source's exact curve, scaled/mirrored/rotated with the glyph. New or moved edges get a fresh curve from the connection rule that made them.
- **Quality gate.** The derived glyph is compared with every glyph **except** its source using `quality.similarity.max`, and with the source using `maxSimilarityToSource` (0-1, default `0.9`; `1` turns it off). So it can never be a copy. Similarity is measured on the node positions (shifted to the top left), so deleting edges but keeping their nodes (`removeOrphanNodes: false`) leaves it identical in that measure.

| Option | Default | Notes |
|---|---|---|
| `from` | (required) | The glyph to start from. |
| `steps[]` | (required) | Run in order, each described below. |
| `maxSimilarityToSource` | `0.9` | See above. |

### Steps

Every step has `op`, and optionally `onlyIf` and `chance`. Options that don't belong to the op are rejected.

- **`onlyIf`** is a gate: `{ "minEdges": 4, "maxEdges": 9, "minNodes": 5, "maxNodes": 12 }`, every limit optional, all that are set must hold. It looks at the glyph **as it is when the step comes up**, after the earlier steps, so a step can depend on what came before it (for instance, only delete more if enough is left). Nodes are the distinct points, lone ones included; a circle or dot counts as one edge. A step whose gate is closed does nothing and uses no random numbers.
- **`chance`** (0-100, default 100) is the chance that the step runs at all. It is rolled only if the gate is open.

**Amounts.** `DeleteNodes`, `DeleteEdges`, `Keep` and `Nudge` take `amount`: exactly one of `percent` `{min,max}` (of **all** nodes, or all edges, existing when the step starts, rounded) or `count` `{min,max}` (a fixed number, drawn inclusive).

| `op` | Options | What it does |
|---|---|---|
| `DeleteNodes` | `amount`, `target`, `mode`, `connectionRules`, `bridgeMaxMoves`, `removeOrphanNodes`, `keepAtLeast`, `keepConnected` | Removes nodes and the edges touching them. |
| `DeleteEdges` | `amount`, `removeOrphanNodes`, `keepAtLeast`, `keepConnected` | Removes edges. |
| `Keep` | same as `DeleteEdges` | The opposite: `amount` is how many edges **stay**; the rest go. |
| `Add` | `shapes`, `optional` | Adds strokes from entries of `shapes` (by `id`), placed with their own count, chain, placement and arrangement against what is left. Added strokes cross the existing ones only if the shape's `arrangement.crossing` is true. A shape that can't be placed fails the glyph unless `optional` is true. |
| `Scale` | `factors` | Scales the whole glyph about its middle by one of the weighted ratios `{numerator, denominator, weight}`. Nodes snap to the grid (so nodes can merge, and a straight edge collapsing to a point is dropped). A ratio that would not fit the grid is not picked; if none fits the step is skipped. |
| `Mirror` | `axis` | `Horizontal` (left becomes right, default), `Vertical` or `Random`. |
| `Rotate` | `degrees` | One of 90, 180, 270 (default all three), then slid back into the grid; skipped if it cannot fit. |
| `Nudge` | `amount`, `target`, `distance` | Moves nodes by up to `distance` (default 1) on each axis, never onto another node or into a crossing. Moved edges lose their source curve. |

**`target`** (DeleteNodes, Nudge): `Any` (default), `Ends` (free tips, one edge end), `Inner` (two edge ends: the middle of a stroke) or `Junction` (three or more edge ends meet).

**`mode`** (DeleteNodes): `Cut` (default) removes the node and its edges, so a stroke through it splits in two. `Bridge` reconnects the two neighbours of an `Inner` node with a **new path grown from the step's `connectionRules`** (same fields as `chain.connectionRules`; Circle rules, angle limits and loop chances are ignored here): at most `bridgeMaxMoves` moves (default 3), shortest first, crossing nothing and avoiding the glyph's other nodes. If no path exists, or the node is a tip or junction, it is cut instead.

**Safety options.** `keepAtLeast` (default 1): never end with fewer edges than this; a pick that would is skipped. `keepConnected` (default false): skip any pick that would split the glyph into more connected pieces than before the step. `removeOrphanNodes` (default true): nodes left with no edge are removed; with false they stay as lone nodes (invisible when drawn, shown by the debug view, and still counted by later steps and by similarity).

Circles and dots are single edges: deleting either end node, or the edge, removes the whole circle.

---

## 11. Curves

How an edge bends is set **per connection rule**, in the rule's `curve` block, so one stroke can mix gentle curves and dead-straight ticks. The top-level `curve` block only holds what is not per edge: `edge` (the settings for edges that no rule made, i.e. Literal shapes), the proximity guard, `forceAllEdgesCurved` and `contextMode`.

Each edge is a straight line or a cubic bezier. Curves are planned while the glyph is generated (once the whole chain is known) and stored on the edges, so copies, derivations, composites and the renderer all use the same curves. Copies mirror their original's curves; a derived glyph keeps the curve of every edge it did not move; a moved or new edge is planned again from the rule that made it.

```json
"connectionRules": [
  { "direction": "Orthogonal", "minLength": 1, "maxLength": 5, "weight": 3,
    "curve": {
      "chanceToBeCurved": 40,
      "mode": "Derived",
      "tension": { "options": [ { "min": 0, "max": 0, "weight": 3 }, { "min": 0.4, "max": 0.5, "weight": 2 } ] },
      "explicitJitterDegrees": { "min": 20, "max": 20 }
    } },
  { "direction": "Circle", "minLength": 1, "maxLength": 2 }
]
```

```json
"curve": {
  "contextMode": "AllCurved",
  "edge": { "chanceToBeCurved": 100 },
  "proximityGuard": { "enabled": true, "minEdgeLength": 2 }
}
```

Per rule (`connectionRules[].curve`, and `curve.edge`):

| Option | Notes |
|---|---|
| `chanceToBeCurved` | Default `0` (every edge straight). Percentage 0-100: the chance that an edge made by this rule is a curve rather than a straight line, rolled per edge. (This replaces the old language-wide `straightLineBias`: `chanceToBeCurved = 100 - straightLineBias`.) Set it above 0 to get curves; 100 still looks straight if the tension is 0. |
| `mode` | Default `Derived` (tangents from the neighbouring nodes, smooth); `Explicit` (the two handles of the edge deviate independently, kinks possible); `Hybrid` (each edge randomly Derived or Explicit). |
| `tension.range` `{min,max}` | Default `0.25..0.35`. Used **only when `options` is empty**: one continuous range, sampled uniformly. |
| `tension.options[]` | Default empty. Weighted bands `{min, max, weight}` (weight default `1`); one band is picked per handle, then a value is sampled inside it. This is what allows bimodal looks such as "70% dead straight (`0..0`), 30% strongly curved (`0.4..0.5`)". When non-empty it replaces `range`. The final value is clamped to `0..0.5` and is the handle length as a fraction of the edge length. |
| `explicitJitterDegrees` `{min,max}` | Default `20..60`. Maximum angular deviation of explicit handles, in degrees. Only matters in `Explicit`/`Hybrid`. |

Circles and dots ignore all of this: a circle is always drawn round, a dot is always a disc. Where the two edges around a node come from different rules, each uses its own settings; the handle directions at the node are still derived from the neighbouring nodes, so the join stays smooth.

Language level (`curve`):

| Option | Notes |
|---|---|
| `edge` | Default: always curved, `Derived`, tension `0.25..0.35`. Used for edges no rule made (Literal shapes). |
| `proximityGuard.enabled` | Default `false`. Forces edges shorter than `minEdgeLength` straight; it changes how many edges end up curved. |
| `proximityGuard.minEdgeLength` | Default `1.5`. Edges shorter than this (grid units) are forced straight. |
| `forceAllEdgesCurved` | Default `false`. Every edge is a curve, ignoring every `chanceToBeCurved` and the guard. Rejected if any chance is below 100 or the guard is on. |
| `contextMode` | Default `AllCurved`. Curved or straight, for glyphs on their own versus inside composites (below). |

### `contextMode`

"Mini" = a plain glyph rendered on its own. "Combined" = a glyph's components inside a composite (a composite rendered on its own also counts as combined).

| Value | Mini | Combined |
|---|---|---|
| `AllCurved` (default) | curved | curved |
| `AllStraight` | straight | straight |
| `CurvedMiniStraightCombined` | curved | straight |
| `StraightMiniCurvedCombined` | straight | curved |

- "Curved" means the glyph's normal curve plan: each rule's `chanceToBeCurved` still decides edge by edge which edges are lines. It does **not** force every edge to bend.
- "Straight" makes every edge a straight line.
- Wherever a glyph is curved, its curves are the **same** in both contexts: a component reuses its source glyph's plan, mapped through the component's scale, rotation and translation.
- Reused curves are not re-checked against neighbouring components, so a bulge can reach into an adjacent component. `CurvedMiniStraightCombined` avoids that.
- If a component's source glyph can't be matched to its stored geometry, that layer falls back to being planned from the composite's own seed.

---

## 12. Rendering

Block: `render` (pixel size and margin come from `render.canvas`). All of a glyph's ink is filled as **one path**, so overlaps and crossings come out solid even with a semi-transparent colour.

```json
"render": {
  "framing": "Language",
  "foreground": "#FFFFFFFF",
  "background": "#00000000",
  "canvas": { "sizePx": 512, "marginPx": 32 },
  "transform": { "rotateDegrees": 0, "rotateOptions": [] },
  "debug": { "showNodes": false, "colorComponents": false, "colorParts": false, "colorShapes": false, "showUndrawn": false },
  "typeface": {
    "kind": "Rectangular",
    "perGlyphJitter": 0.0,
    "rectangular": {
      "width": { "min": 0.045, "max": 0.045 },
      "cap": "Butt"
    }
  },
  "joins": {
    "corner":   { "outer": { "style": "Miter", "miterLimit": 4 }, "inner": { "style": "Overlap" } },
    "junction": { "outer": { "style": "Round" }, "inner": { "style": "Round", "size": 0.5 } }
  }
}
```

| Option | Notes |
|---|---|
| `framing` | How glyphs are scaled and positioned on the canvas (default `Grid`): `Grid` = the whole grid maps to the canvas, so a glyph's size and position on the grid carry through; `Glyph` = crop to each glyph's own bounding box and centre it (relative sizes are lost); `Language` = crop to the combined bounding box of every glyph in the language, one shared scale for all; `GlyphCentered` = the grid's scale, but each glyph re-centred on the canvas. |
| `canvas.sizePx`, `canvas.marginPx` | The output image size (square, minimum 16) and the space around the drawing; see section 3. |
| `transform.rotateOptions` | Per-glyph random turn **added to** `transform.rotateDegrees`: a list of `{ "min", "max", "weight" }` ranges in degrees. Each glyph draws one option by weight (default 1, 0 = never), then an angle uniformly from its range. The draw comes from the glyph's own seed, so a glyph always turns the same way, in normal and debug images alike. Example: `[ { "min": -8, "max": 8, "weight": 4 }, { "min": 85, "max": 95, "weight": 1 } ]` mostly tilts a little and now and then lays a glyph on its side. A derived glyph can take its source's angle (followed back through such derivations) so the two stay turned alike; the derivation's `finalOutputRotation` decides when (see Derivations). Empty (default) = none. |
| `transform.rotateDegrees` | Turns every glyph by this many degrees, **clockwise**, around the middle of its grid (default `0`); the canvas stays as it is. Any angle works and curves turn exactly. The framing then fits the turned glyphs: with `Grid` framing the grid's square is what turns, so its corners can reach past the canvas margin; `Glyph` and `Language` framing rescale to fit the turned shapes. Applies to the normal and the debug images (lattice, labels and node dots turn too). |
| `foreground`, `background` | `#RRGGBB` or `#AARRGGBB`. Defaults: foreground `#000000`, background `#00000000` (transparent). Unparseable values fall back to black / transparent. |
| `debug.showNodes` | Draws the faint lattice with coordinate labels every 5 cells, plus node dots: red = a shape with a `count`, blue = a shape without one, **gold = a node used more than once** (a revisit or a node shared by shapes). Extra nodes the stroke only passes (circle outline nodes, `split` mode `ExtraNodes`) are drawn as smaller dots. |
| `debug.colorComponents` | Fills each combination component in its own colour. |
| `debug.colorParts` | Colours the ink each kind of part adds: **grey** caps (and Ball / Ink terminals), **violet** corner joins, **pink** junction joins (including straightening). Strokes keep the foreground (or component) colour. Each part shows only where it goes beyond the strokes, painted over them in build order: corner joins, junction joins, caps. A cap removed by `trimCaps` is not drawn, so it is not shown. |
| `debug.colorShapes` | Gives every shape its own colour in the `.debug.png` images, in the order of the shapes list. Instances of one shape (a shape with a `count`, or a copy of each instance) share a hue and differ in lightness, so they can be told apart. Joins between strokes keep the foreground colour; chains with no shape (derived strokes from before this option, circles' joins) are grey. Takes the place of `debug.colorComponents` when both are on, and combines with `debug.colorParts` (parts are then drawn over each shape's colour). |
| `debug.showUndrawn` | Draws the shapes that have `isDrawn: false` in the `.debug.png` images, in one translucent orange. They are never in the normal images. |
| `typeface.kind` | `Rectangular` (default), `ThickEnds` or `Brush`. Selects which sub-block below is used; a missing sub-block means that typeface's defaults. |
| `typeface.perGlyphJitter` | `0` (default) = every glyph uses the identical typeface; `0.1` = each glyph's widths vary by up to +-10%. Clamped to 0-0.9. |

### Framing and padding

- The grid-to-pixel mapping is **uniform** (one scale for both axes), so shapes are never distorted. `framing` (above) chooses what gets scaled and centred.
- The drawing is fitted to the canvas minus a padding on every side: `render.canvas.marginPx` plus the typeface's largest ink extent (including `perGlyphJitter`) plus 1 px, so strokes at the edge don't clip. Bounds come from the actual sampled curves, not just the nodes.
- A minimum extent of one grid unit is used when scaling, so a lone straight line isn't blown up without limit.
- Stroke widths are fixed in pixels (canvas fraction x canvas size), so framing never changes stroke weight: a zoomed-in crop has the same thickness as a full-grid view.

### Joins

Block: `render.joins`, shared by every typeface. Ink is built from many small convex pieces (one quad per segment, plus a wedge or fan per join and cap) filled together, so overlaps and crossings come out solid.

```json
"joins": {
  "corner":   { "outer": { "style": "Miter", "miterLimit": 4 }, "inner": { "style": "Overlap" } },
  "junction": {
    "outer": { "style": "Round" },
    "inner": { "style": "Round", "size": 0.5 },
    "byAngle": [
      { "angle": { "min": 0, "max": 60 }, "outer": { "style": "Bevel" }, "inner": { "style": "Overlap" } }
    ]
  }
}
```

**Where joins happen.** At every grid node, the strokes leaving it are sorted by direction, and each gap between neighbouring strokes gets a join:

- a gap **over 180 degrees** is the convex outside of a bend: the **outer** join adds a point, bevel or arc;
- a gap **under 180 degrees** is a notch between two strokes: the **inner** join can fill it with a chamfer or fillet;
- a gap of exactly 180 degrees is a straight run and gets nothing.

A bend has one gap of each kind; a T, a Y or a crossing only has notches. Which of the two blocks applies depends on the node: **`corner`** is a bend inside one stroke (two arms of the same stroke), **`junction`** is everything else (tip to tip, a tip on another stroke, a crossing, a stroke passing its own node again). Both use the widths of the strokes at that node. Points in the middle of a curved edge are not nodes: they only get the `corner` outer join, so curves stay smooth. Joins are built within one layer, so components of a composite are not joined to each other.

| Option | Default | Notes |
|---|---|---|
| `corner`, `junction` | see below | Each has `outer`, `inner` and `byAngle`. |
| `outer` | `Miter` (corner and junction) | `style`, plus `miterLimit` for Miter. |
| `inner` | `Overlap` | `style`, plus `size` for Bevel and Round. |
| `byAngle[]` | `[]` | Overrides, see below. |
| `junction.trimCaps` | `false` | Junction only. See below. |
| `junction.straightenInnerDegrees` | `0` | Junction only; `0` = off. See below. |
| `junction.straightenOuterDegrees` | `0` | Junction only; `0` = off. See below. |
| `junction.straightenLength` | `4` | Junction only: length of the straightening fill in half-widths. |

| `style` | Outer side | Inner side |
|---|---|---|
| `Miter` | Sharp pointed corner. Falls back to a bevel when the tip would be longer than `miterLimit` x half-width (minimum 1). | Same as `Overlap`. |
| `Bevel` | Corner cut flat. | A flat chamfer across the notch, `size` half-widths long. |
| `Round` | Rounded corner (a full reversal gets a round cap). | A fillet in the notch with a radius of `size` half-widths. |
| `Level` | The miter point **cut flat** along an axis (`axis`: `Auto`, `Horizontal`, `Vertical` or `Both`; `Both` cuts along the row and the column at once, giving a pointed corner) at the level of the outer corner that reaches furthest, so a V pointing down gets a flat bottom and a corner pointing right a vertical edge. `Auto` cuts across the corner's outward direction. Falls back to a bevel when the corner points too far from the axis to be cut. | Same as `Overlap`. |
| `Overlap` | Nothing added: plain overlapping strokes. | Nothing added: the notch stays sharp. |

The inner `size` is measured in half-widths of the stroke (`1` = half the stroke width) and is limited automatically so the chamfer or fillet never reaches past the neighbouring node. The inner join only ever **adds** ink; it cannot cut away the notch. It follows the strokes' real edges, so it still meets the borders when a stroke curves away from the node.

**`byAngle`.** A list of rules tried in order; the first whose `angle` range (degrees, 0 to 180, inclusive) contains the angle between the two strokes wins. A rule can give `outer`, `inner` or both; what it leaves out falls back to the context's own. 180 is straight on, 90 a right angle, 45 a sharp turn. On the grid the angle is almost always 45, 90 or 135; on curved edges it is measured on the curve tangents at the node, so it varies continuously. A 45 degree miter is only about 2.6 half-widths long, so `miterLimit` alone never turns grid corners into bevels; `byAngle` is the way to do that.

**`trimCaps`** (junction only). When on, a tip that ends at a junction (a node where another stroke is also present) is drawn **without** its cap, `Ball` terminal or `Ink` start blob, and the joins fill the node instead. A tip that stands alone keeps everything. With the junction's `outer` set to `Overlap` nothing fills the node, so use Miter, Bevel or Round with it. Note that a junction is any node with another stroke in it, including a tip landing on the middle of another stroke. This holds across the components of a combination too: a node where two components meet is a junction like any other, joined and trimmed the same way.

**`straightenInnerDegrees` / `straightenOuterDegrees` / `straightenLength`** (junction only). Where two strokes meet within the given number of degrees of a straight line (180), the kink is straightened. The two sides of a nearly straight pair behave differently, so each has its own tolerance (`0` = off):

- **Inner** is the side that bends inwards (a gap under 180 degrees, with no other stroke in it). It is filled with one long chamfer, `straightenLength` half-widths along each edge, so the edge reads as one straight line. It replaces the inner join there.
- **Outer** is the side that bends outwards (a gap over 180 degrees), for example the top of a T whose bar is slightly bent that way. Ink can't be removed, so the two edges are extended along one straight line through their corner (the average of the two directions) and the thin wedges between that line and the real edges are filled. Each wedge ends in a small step; its height grows with the angle and with `straightenLength` (about `length x half-width x tan(angle / 2)`, so keep the angle small or the length short if it shows). It replaces the outer join there.

Whether the top of a T is "inner" or "outer" depends on which way the bar bends, so set both if you want it straightened either way. Other strokes may leave the node on the other side. Both lengths are limited so they never reach past the neighbouring node. A gap within 0.5 degrees of straight needs nothing.

### Caps

Caps (`Rectangular` only; the other typefaces choose their own end shapes): `Butt` ends flat at the node, `Square` extends half a width past it, `Round` ends in a half disc, `Level` cuts the end flat along an axis: the corner that reaches less far is **extended along the stroke** until it reaches as far as the other, so the end sits on one level (row or column). Straight horizontal and vertical strokes are left as they are (their end is already level), and a stroke too shallow to the axis (more than about 45 degrees off) is not changed. With `capLevel: Both` the end is cut along the row **and** the column, so a diagonal end becomes a point (a `>` shape with one horizontal and one vertical face); steep or flat strokes get just the cut that applies. With `capLevelFlat: ExtendToNeighbours` every Level end (flat or diagonal) grows until its face lies half a stroke width past its node's row (or column) - where the edge of a horizontal (or vertical) stroke through that node would be, whether or not such a stroke is present. Ends on one row therefore all stop on one line, level with any horizontal stroke on it, and the result does not depend on what else is in the glyph. Caps are drawn at open tips, under any junction join, unless `junction.trimCaps` removes them at junctions.

### Typefaces

One typeface per **language**, resolved once from the seed, so every glyph reads as the same font. All widths are **fractions of the canvas size**. Width-like options are `{min,max}` ranges, resolved once per language. `perGlyphJitter` then scales every width of a glyph by `1 + jitter x r`, with `r` drawn per glyph in -1..1. Typefaces don't depend on `curve.mode`: on a curved edge you simply get the stroke following the curve.

**`Rectangular`** (`typeface.rectangular`): constant-width strokes.

| Option | Default | Notes |
|---|---|---|
| `width` | `0.05` | Stroke width. |
| `cap` | `Butt` | `Butt`, `Square`, `Round` or `Level`. |
| `capLevel` | `Auto` | With `Level`: the line the end is cut along. `Horizontal` = flat on the node's row, `Vertical` = along its column, `Auto` = across the stroke's dominant direction (a stroke steeper than 45 degrees is cut horizontally, a flatter one vertically; exactly 45 degrees horizontally), `Both` = along the row and the column (a diagonal end becomes a point). |
| `capLevelFlat` | `Keep` | With `Level`: `Keep` leaves ends as they are; `ExtendToNeighbours` grows every Level end (flat or diagonal) until its face lies half a stroke width past its node's row/column, the same level a stroke along that line would reach, whether or not one is present. Skipped at junctions when `junction.trimCaps` removes the cap. |

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

**`Brush`** (`typeface.brush`): `mode` picks the look, and each mode reads its own options.

| Mode | How it draws | Options it reads |
|---|---|---|
| `Taper` | Pointed at both ends, swelling in the middle: width follows `sin(pi x s) ^ taperSharpness` where `s` is the fraction along the whole **chain**. Closed loops are uniform at full width. Round caps if the tip is over half a pixel, otherwise butt. | `width`, `tipWidth`, `taperSharpness` |
| `BroadNib` | Calligraphy pen: a flat nib (length `width`, thickness `thinWidth`) is held at `nibAngleDegrees` and **swept along the stroke**, so the ink is what a real pen leaves: thick across the nib, a hairline along it, changing smoothly through curves and corners with no joins (`render.joins` is ignored). Optional hand-written feel through the `nib…` options. | `width` (nib length), `nibAngleDegrees`, `thinWidth`, `nibRoundness`, `nibAngleWobble`, `nibPressure`, `nibLeadIn` |
| `Ink` | Pressure profile along the stroke, per-side seeded edge wobble, optional dry-brush ragged edges, round caps, and a blob at the start of each open stroke. Seeded per chain, so it is repeatable. | `width`, `pressureVariation`, `edgeWobble`, `raggedness`, `startBlobScale` |

| Option | Default | Notes |
|---|---|---|
| `mode` | `Taper` | `Taper`, `BroadNib` or `Ink`. |
| `width` | `0.06` | Maximum stroke width (`Taper` / `Ink`) or nib length (`BroadNib`). |
| `tipWidth` | `0` | `Taper`: width at the tips. |
| `taperSharpness` | `1.0` | `Taper`: above 1 = pointier, below 1 = fuller (minimum 0.05). |
| `nibAngleDegrees` | `40` | `BroadNib`: angle on screen, clockwise from the +x axis. |
| `thinWidth` | `0.012` | `BroadNib`: nib thickness, i.e. the width of the thin strokes. |
| `nibRoundness` | `0` | `BroadNib`: 0 = flat, square-ended nib; 1 = fully rounded ends (a capsule, like a brush pen). |
| `nibAngleWobble` | `0` | `BroadNib`: degrees (0-90) the hand lets the nib angle drift, slowly and seeded per stroke (a closed stroke meets itself at the seam). |
| `nibPressure` | `0` | `BroadNib`: 0-1, how much the nib length (pressure) varies along a stroke. |
| `nibLeadIn` | `0` | `BroadNib`: 0-0.5, fraction of an open stroke's length at each end over which the nib eases in from a light touch (30%) to full length. |
| `pressureVariation` | `0.35` | `Ink`: how much the width varies along a stroke (0-1). |
| `edgeWobble` | `0.15` | `Ink`: wobble of each edge. |
| `raggedness` | `0` | `Ink`: roughness of the edges. |
| `startBlobScale` | `1.4` | `Ink`: above 1 adds a blob where each open stroke starts. |

Example of the other kinds:

```json
"typeface": { "kind": "ThickEnds", "thickEnds": { "width": { "min": 0.03, "max": 0.03 }, "endWidth": { "min": 0.07, "max": 0.07 }, "terminal": "Ball" } }
"typeface": { "kind": "Brush", "brush": { "mode": "BroadNib", "nibAngleDegrees": { "min": 40, "max": 40 } } }
"typeface": { "kind": "Brush", "brush": { "mode": "BroadNib", "nibRoundness": 0.7, "nibAngleWobble": 5, "nibPressure": 0.25, "nibLeadIn": 0.15 } }   // handwriting feel
```

The CLI always writes an extra `.debug.png` per glyph with node dots and component colours on, whatever these two debug flags say.

---

## 13. Output files

```
<run.outputDirectory>/
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
- The contact sheet lists real glyphs in the order of `glyphs`, at each glyph's own pixel size, edge to edge: no margins, gaps or frames, no labels. Columns come from `run.contactSheet.columns`. It uses `render.background` exactly as configured; if that is transparent or translucent, so is the sheet.
- The console also prints an ASCII preview of every glyph (straight lines only), marking base glyphs.
- PNGs can differ slightly between platforms and Skia versions because of anti-aliasing; geometry is deterministic, but don't expect byte-identical files.

---

## 14. Config validation

Errors aim to say what to change. A value of the wrong kind (a misspelled name, text where a number belongs) is reported with its path, line and column, what was written, and what is allowed. When generation fails, the message lists what rejected the placements: for a copy shape, a composite recipe component (per component: positions tried, left the grid, crossed, broke a side rule or spacing), a derivation (which step removed the last edge), or the quality gate (a hint that fits the cause, including `maxSimilarityToSource` for derived glyphs).

`RootConfigLoader` works in three steps and reports **all** problems of the failing step at once (`Invalid config: - ...`):

1. **Structure** (on the raw JSON): unknown properties (with a "did you mean" hint), unexpected `null`s, and the per-language `grid`. An option that is not part of the current layout is simply an unknown property.
2. **Recipe overrides** are merged into `combination.arrangement` (section 10).
3. **Values**:

| Area | Rule |
|---|---|
| Grid | No top-level `grid`; every language has its own; width and height >= 1. |
| Glyph names | Non-empty; unique across `glyphs` and `baseGlyphs`. |
| Recipes | A base glyph can't be a recipe key; a recipe needs components; every component must be listed in `glyphs` or `baseGlyphs`; a composite built from a composite must come after it in `glyphs` (which also rules out cycles). |
| `derivations` | Each name listed in `glyphs`/`baseGlyphs`, with no recipe; `from` listed and not itself, no circles through derivations and recipes; at least one step; each step's options valid for its op (exactly one of `percent`/`count`, ids in `shapes` exist, Bridge has rules, ratios positive, rotations 90/180/270); `maxSimilarityToSource` 0-1. |
| `shapes` | At least one when there are plain/base glyphs (derived glyphs don't count). Each `id` non-empty and unique; `seedLabel` unique. `count` >= 0 with `max` >= `min`. Literal: `nodes` present, >= 2 points, no repeated consecutive point. Generative: no `nodes`; `chain.length` >= 2; at least one valid `connectionRules` entry (valid direction, `minLength` >= 1 (0 for `Circle`), `maxLength` >= `minLength`, weights >= 0 and not all 0, angles within 0-180 with `maxAngle` >= `minAngle`, no preset together with explicit angles, `Angle` has a limit, `Circle` has none, `backToStartChance` and `chanceToBeCurved` within 0-100, `chainLengthCost` 0 or more, valid `curve` block); each `selfSpacing` rule valid. |
| `chance` | Number from 0 to 100. |
| `copyOf` | `shape` names an earlier shape (not itself); known `flip`/`auto`/`anchor`; `flips` only with `Random`, not empty, `auto.shapes` only with `Auto` and naming earlier shapes, no `Random`/`Auto` inside; `gap` max >= min; `retries.base` and `retries.glyph` >= 0; `shift` >= 0; no `count`, `nodes`, `chain` rules or `placement` on a copy entry; in a derivation step the original is listed first. |
| `placement` | Known `start.mode`; `Fixed` needs a `start.origin` on the grid (and inside `area.bounds`), other modes must not have one; `retries.placement` >= 1, `retries.shape` and `retries.glyph` >= 0; `rotation.angles` only on Literal shapes, with `rotation.allowed` on, using only 0/90/180/270; `start.onExistingNode` only with `RandomValid`, `chance` 0-100, `shapes` naming earlier shapes, and not with `arrangement.spacing.minNodeDistance` > 0 (at `chance` 100) unless `ignoreStartNode` is on; node masks: each region sets exactly one kind, `nodesOfGlyph` names a listed glyph without cycles. |
| `arrangement` | Spacing distances (including `minEdgeDistance`, shapes only) finite and >= 0, `maxNodeDistance` >= `minNodeDistance`, valid `maxDistanceMode`; known bias modes, no `Tallest`+`Shortest` or `Widest`+`Narrowest`, bias weights valid; `candidates` >= 1; `includeScaleAndRotation` not on a shape. Checked for every shape, for `combination.arrangement` and for each recipe override. |
| `placement.side` | Only with `start.mode: RandomValid`; `Prefer` needs one or two sides, none cancelling, and no `pick`; `SetRandom` takes no `sides`; `weight` >= 0. |
| `placements` | The glyph must be listed in `glyphs` or `baseGlyphs` (in a recipe's own arrangement: be one of its components) and have only one rule; `Prefer` needs one or two sides, none cancelling, and no `offset` or `pick`; `SetRandom` takes no `sides` or `offset`; `Fixed` takes no `sides`, `pick` or `scope`; `weight` >= 0. |
| `combination` | `retries` >= 1; scale numerators/denominators positive; rotation angles 0/90/180/270. |
| `render.joins` | Known styles; `miterLimit` >= 1; `size` >= 0; `straightenInnerDegrees` and `straightenOuterDegrees` 0..90; `straightenLength` >= 0; each `byAngle` rule has an angle range within 0..180 with `max` >= `min`, and sets `outer` and/or `inner`. |
| `usageSizeBias` | `strength` is finite and >= 0. |
| `run`, `render.canvas`, `render.transform`, `quality` | `run.outputDirectory` not empty; `run.maxParallelism` 0 or more; `run.contactSheet.columns` >= 1; `canvas.sizePx` >= 16, `marginPx` >= 0 and leaving room; `rotateDegrees` finite, `rotateOptions` with a positive weight somewhere; `similarity.max` and `compositeMax` 0-1; `quality.retries` >= 1. |

Generation-time errors you may meet:

- *"Shape 'x' (instance i/n) for glyph 'g' could not be placed"*: the message says which kind of failure dominated. Loosen `arrangement` (crossing, spacing), the chain rules, or raise `placement.retries.placement` (`retries.shape` and `retries.glyph` retry with fresh draws; all three default to 100).
- *"Could not generate a sufficiently distinct, non-trivial glyph"*: raise `quality.retries`, raise `quality.similarity.max`, or widen the `count` / `chain.length` ranges.
- *"Could not satisfy combination recipe"*: allow scale/rotation, raise `combination.retries`, relax `combination.arrangement` (or that recipe's), or use a larger grid.
- *"Circular combination dependency"*: two recipes depend on each other's components.

---

## 15. Recipes and interactions

**Compact, connected glyphs.** Strokes that cluster instead of scattering:

```json
"arrangement": { "spacing": { "enabled": true, "maxNodeDistance": 3, "maxDistanceMode": "NearestShape" } }
```

**Strokes that branch from each other.**

```json
"placement": { "start": { "onExistingNode": {} }, "retries": { "placement": 30 } },
"arrangement": { "crossing": { "mode": "Anywhere" } }
```

**Airy strokes that don't fold back on themselves.**

```json
"chain": { "selfSpacing": [ { "mode": "Existing", "minDistance": 1.4 } ] }
```

**A motif nothing may cross, with free scribbles around it.** Give the motif `crossing.mode: None` and the scribbles `true` (section 7).

**Long strokes plus short ticks.** Two counted shapes with different `chain.length` and `connectionRules`.

**Square-ish glyphs.** `"arrangement": { "bias": { "modes": [ "Squarest" ] }, "candidates": 4 }` on a stroke shape; the same `bias` under `combination.arrangement` for composites.

**One recipe that may overlap.** `"fortress": { "components": [...], "arrangement": { "crossing": { "mode": "Anywhere" } } }`.

**Angular script with flowing composites.**

```json
"curve": { "contextMode": "StraightMiniCurvedCombined" }
```

**Keep helper glyphs out of the atlas.** List them under `baseGlyphs`, reference them in `recipes`.

Interactions to keep in mind:

- Spacing applies to composites too, through `combination.arrangement`; it is separate from the spacing on shapes.
- Spacing is order-dependent: a shape's rules are checked against the shapes before it, so give the shape that should "stay close" the `maxNodeDistance`.
- `start.onExistingNode` and `minNodeDistance > 0` cannot coexist on the same shape, unless `onExistingNode.ignoreStartNode` is true (it leaves the shared start node out of the check), or every shape in `placement.startOnShapes` is left out of the spacing by `onlyShapes` / `excludeShapes`.
- `usageSizeBias` and `quality.similarity.max` both influence how varied plain glyphs are; strong low-size bias plus a strict similarity limit can make distinct glyphs harder to find.
- The rotation settings live under `combination` but also feed placement of Literal shapes unless the shape lists its own `rotation.angles`; see section 6.
- With `crossing.mode: None` on every shape, no stroke can cross another, which makes dense glyphs on small grids hard to place. Edges may still meet at shared nodes.
- A bias with many `candidates` on a tight grid multiplies the placement work per shape.

---

## 16. Full example

A valid config using the current schema. Everything not shown takes its default. `sample-config.json` and `sample-config-advanced.json` next to the CLI are complete, working examples.

```json
{
  "run": { "seed": 20260927, "outputDirectory": "./output" },
  "languages": [
    {
      "name": "Example",
      "grid": { "width": 15, "height": 15 },

      "baseGlyphs": [ "shield_base" ],
      "glyphs": [ "warrior", "weapon", "protect", "fortress" ],

      "shapes": [
        {
          "id": "bar",
          "kind": "Literal",
          "nodes": [ [0, 0], [3, 0] ],
          "placement": { "start": { "mode": "CenterAnchor" } },
          "arrangement": { "crossing": { "mode": "None" } }
        },
        {
          "id": "strokes",
          "count": { "min": 1, "max": 3 },
          "chain": {
            "length": { "min": 5, "max": 8 },
            "connectionRules": [
              { "direction": "Orthogonal", "minLength": 1, "maxLength": 4, "weight": 3.0, "backToStartChance": 15,
                "curve": { "chanceToBeCurved": 40,
                           "tension": { "options": [ { "min": 0, "max": 0, "weight": 3 }, { "min": 0.4, "max": 0.5, "weight": 1 } ] } } },
              { "direction": "Diagonal",   "minLength": 1, "maxLength": 3, "weight": 1.0 }
            ],
            "connectionWeighting": "PerRule",
            "allowClosedLoop": true,
            "selfSpacing": [
              { "mode": "Existing", "minDistance": 1.4 },
              { "mode": "AllNodes", "maxDistance": 7 }
            ]
          },
          "placement": { "start": { "onExistingNode": {} }, "retries": { "placement": 30 } },
          "arrangement": {
            "crossing": { "mode": "Anywhere" },
            "spacing": { "enabled": true, "minCenterDistance": 1, "maxNodeDistance": 4, "maxDistanceMode": "NearestShape" },
            "bias": { "modes": [ "Squarest" ] },
            "candidates": 3
          }
        }
      ],

      "usageSizeBias": { "strength": 0.5, "affectCount": true, "affectLength": true },

      "quality": { "similarity": { "max": 0.7 }, "rejectStraightLineOnly": true, "retries": 100 },

      "combination": {
        "recipes": { "fortress": [ "warrior", "shield_base" ] },
        "allowScale": true,
        "allowedScales": [
          { "numerator": 1, "denominator": 1, "weight": 2.0 },
          { "numerator": 2, "denominator": 1, "weight": 1.0 }
        ],
        "allowRotate": true,
        "rotation": { "allowed": [ 0, 90, 180, 270 ], "weight": 1.0 },
        "retries": 100,
        "arrangement": {
          "bias": { "modes": [ "Squarest" ], "includeScaleAndRotation": false }
        }
      },

      "curve": {
        "contextMode": "CurvedMiniStraightCombined",
        "proximityGuard": { "enabled": true, "minEdgeLength": 2 }
      },

      "render": {
        "framing": "Language",
        "foreground": "#FFFFFFFF",
        "background": "#00000000",
        "canvas": { "sizePx": 512, "marginPx": 32 },
        "debug": { "showNodes": false, "colorComponents": false },
        "joins": {
          "corner":   { "outer": { "style": "Miter", "miterLimit": 4 }, "inner": { "style": "Overlap" } },
          "junction": { "outer": { "style": "Round" }, "inner": { "style": "Round", "size": 0.5 } }
        },
        "typeface": {
          "kind": "Rectangular",
          "perGlyphJitter": 0.0,
          "rectangular": { "width": { "min": 0.045, "max": 0.045 }, "cap": "Butt" }
        }
      }
    }
  ]
}
```
