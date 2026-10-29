# GlyphGenerator

GlyphGenerator generates invented writing systems. You describe a "language" in a JSON config: a lattice, the kinds of strokes allowed, how glyphs combine into composites, and what counts as good enough. GlyphGenerator draws a set of glyphs that obey those rules and renders them to PNG, with a contact sheet.

Everything is **deterministic**: one seed drives the whole run, so the same config and seed always give the same glyphs, whatever the thread count.

If you run [`configs\all_chants_languages.json`](configs\all_chants_languages.json) you should expect glyphs below:

<p align="center">
  <img src="wiki/img/alchemist%20glyph%20view.png" alt="Alchemist" width="150">
  <img src="wiki/img/anchorites%20glyph%20view.png" alt="Anchorites" width="150">
  <img src="wiki/img/bard%20glyph%20view.png" alt="Bard" width="150">
  <img src="wiki/img/devotee%20glyph%20view.png" alt="Devotee" width="150">
  <img src="wiki/img/warrior%20glyph%20view.png" alt="Warrior" width="150">
</p>


## What it does

- **Grows glyphs on a lattice.** A glyph is built from *shapes*: guaranteed motifs or free-form strokes, each with its own chain rules (length, directions, angles, curves), placement, spacing and crossing rules.
- **Combines glyphs.** Composite glyphs are built from component glyphs by *recipes*, searched for an arrangement that fits (spacing, contact, crossing). A glyph can instead be drawn *against* its partner glyphs (`combination.partners`), so contact holds by construction.
- **Derives glyphs.** A glyph can be made by editing another one (mirror, rotate, split edges, copy, and so on).
- **Checks quality.** Glyphs that are too similar, a single straight line, or that break a quality rule are redrawn, including the components they depend on.
- **Renders.** Rectangular, broad-nib and other typefaces, join styles, framing, per-glyph rotation, and optional debug overlays (nodes, components, parts, shapes). Output is one PNG per glyph plus a contact sheet.
- **Runs in batches.** Several configs, many seeds, in parallel, with a success summary.

The full option reference is in [`GlyphGen-Config-Guide.md`](GlyphGen-Config-Guide.md). A JSON Schema for editor completion is in [`GlyphGen.Cli/config.schema.json`](GlyphGen.Cli\config.schema.json), and two examples are [`GlyphGen.Cli/sample-config.json`](GlyphGen.Cli\sample-config.json) and [`sample-config-advanced.json`](GlyphGen.Cli\sample-config-advanced.json).

More ready to go configs can be found in [configs](configs) folder.

## Using a release (no programming needed)

You do not need to install or build anything to use a release. You download the program, optionally a ready-made set of configs, and run it.

### What to download

- **The program:** `GlyphGenerator.exe` for Windows, or `GlyphGenerator` for Linux (link to the releases page to be added).
- **Optionally, a configs zip:** ready-made language descriptions to try. Without it you need a config of your own (see [`GlyphGen-Config-Guide.md`](GlyphGen-Config-Guide.md)).

### Set it up

1. Make a new, empty folder anywhere, for example `GlyphGen` on your desktop.
2. Put the program in it.
3. If you downloaded the configs zip, unzip it into the same folder. You should now see a folder called **`configs`** next to the program, with `.json` files inside. (Your own config files go in that folder too: any file ending in `.json` that describes a language is picked up.)

### Run it

- **Windows:** double-click `GlyphGen.Cli.exe`. If Windows says it "protected your PC" (the program is not signed), click **More info**, then **Run anyway**.
- **Linux:** the first time, allow the file to run: right-click it, open *Properties*, and tick *Allow executing file as program* (or, in a terminal in that folder: `chmod +x GlyphGen.Cli`). Then double-click it, or start it with `./GlyphGen.Cli` in a terminal.

A black window opens and the program draws the glyphs for every config in `configs`, one language set per config. A small config takes seconds, a big one a minute or more. When it is done, the window closes by itself.

### Find your glyphs

A new folder called **`output`** appears next to the program. Inside it:

```
output
  my-config            <- one folder per config file
    1234567890         <- one folder per run (the number is the seed, see below)
      MyLanguage       <- the language name from the config
        Sun.png        <- one picture per glyph
        Moon.png
        _contact-sheet.png   <- all glyphs on one picture: open this first
        _bases            <- helper glyphs the others are built from (can be ignored)
```

Every run gets its own numbered folder, so nothing is ever overwritten.

### Getting the same glyphs again, or new ones

The **seed** is the number that decides how everything is drawn. The same config with the same seed always gives exactly the same glyphs.

- If the config file contains a line like `"seed": 20260927`, you get the same glyphs every time. To get different ones, change the number, or delete the whole line to get a new random set on every run.
- If it has no seed line, each run uses a new random seed. The folder name in `output` tells you which one it was, so you can put `"seed": <that number>` into the config to keep a result you like.

To edit a config, open the `.json` file in any text editor (Notepad is fine) and keep the quotes, commas and brackets as they are.

### If something goes wrong

The window closes when the program finishes, so you cannot read its messages. To read them, open a terminal in the folder instead:

- **Windows:** open the folder in Explorer, click the address bar, type `cmd` and press Enter, then type `GlyphGen.Cli.exe` and press Enter.
- **Linux:** open a terminal in the folder and type `./GlyphGen.Cli`.

What you may see:

- **"No GlyphGen config found":** there is no `configs` folder next to the program, or nothing valid in it.
- **A list of problems with a config:** the config has a mistake. Each line says where in the file it is and what is wrong. Nothing was drawn.
- **"Generation failed" with a long message:** the config's rules were too tight to draw some glyph with that seed. The message says which part ran out of room. Try again with a different seed, or loosen that part of the config.

Everything else the program can do (many seeds at once, choosing the output folder, running one config only) is described under [Command line](#command-line) below.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Rendering uses [SkiaSharp](https://github.com/mono/SkiaSharp) 2.88.9 (restored from NuGet). On Linux, also add the `SkiaSharp.NativeAssets.Linux.NoDependencies` package to `GlyphGen.Rendering` (a comment in its `.csproj` shows the line).

## Build and run

```bash
dotnet build -c Release

# one config, one run (uses the config's run.seed, or a random seed)
dotnet run -c Release --project GlyphGen.Cli -- GlyphGen.Cli/sample-config.json

# with no arguments: the ./configs (or ./config) folder, else the configs in the current folder
dotnet run -c Release --project GlyphGen.Cli
```

Release builds are much faster than Debug for generation.

### Command line

```
GlyphGen.Cli [config.json | folder | glob ...] [options]
GlyphGen.Cli --schema [config.schema.json]
```

| Option | Meaning |
|---|---|
| `--seed 7`, `--seed 1-20`, `--seed 3,7,11` | Run these seeds (overrides `run.seed`); the same seeds for every config |
| `--runs N` | N runs with random, distinct seeds (not with `--seed`) |
| `--master-seed X` | Draw the random seeds from X; a batch prints its master seed so it can be repeated |
| `--threads N\|auto` | Thread budget (default `auto`, every core available) |
| `--parallel-runs N`, `--threads-per-run N` | Override how the budget is split between runs |
| `--out DIR` | Write to `DIR/<config>/<seed>/<language>/` (default: under each config's `run.outputDirectory`) |
| `--no-render` | Generate only, no images (fast, for measuring how often a config succeeds) |
| `--fail-fast` | Start no new run after a failure |
| `--validate-only` | Check the configs and stop |
| `--summary-json FILE` | Write every run's result to FILE |

A single run prints an ASCII preview and the files written. A batch prints one line per run and a summary per config (runs ok, min/median/max time, failing seeds with the first line of each error). The exit code is 1 if any run failed.

Examples:

```bash
# 20 random seeds of every config in ./configs, using all cores, summary at the end
dotnet run -c Release --project GlyphGen.Cli -- --runs 20 --no-render

# reproduce one failing seed with full output
dotnet run -c Release --project GlyphGen.Cli -- configs/alchemist.json --seed 5
```

## Repository layout

| Project | Purpose |
|---|---|
| `GlyphGen.Core` | Config model and validation, lattice, generation, combination, quality checks. No rendering dependency. |
| `GlyphGen.Rendering` | SkiaSharp renderer, typefaces, joins, contact sheet |
| `GlyphGen.Cli` | Command line: config discovery, seeds, batch runner, output |
| `GlyphGen.SelfCheck` | Console test suite: unit checks, config error messages, seed-stability baselines, legacy-config upgrades, CLI batch logic |
| `GlyphGen.RenderCheck` | Checks that multi-threaded rendering equals single-threaded rendering |

## Tests

```bash
dotnet run --project GlyphGen.SelfCheck          # all checks; exit code 0 = pass
dotnet run --project GlyphGen.SelfCheck -- --record   # re-record seed-stability baselines (only on purpose)
dotnet run --project GlyphGen.RenderCheck
```

The seed-stability baselines were recorded from a Debug build; a Release build can differ in floating-point output, so run the suite in Debug.

## Status and known limits

- Generation can fail for a config and seed that is too tight (the error says which shape or recipe ran out of room and how often it was retried). Tune the retry budgets, clearances or shape chances, or try other seeds; `--runs N --no-render` shows how often a config succeeds.
- Per-run timeouts are not implemented yet (generation cannot be cancelled).

## Contributions

If you want to enchant functionalities of this project, or fix bugs you encountered fell free to do so.

New brushes, caps are first what comes to mind, but generational changes are welcome to if it's limited by current version.

Make a PR we will test it and merge after discussion.

## AI disclosure

This project was developed with use of AI (Claude).

I was interested in end result, not how the code inside works.

The glyphs produced by the program come from the author's configs and the generator's random process; no AI model is involved when the program runs.
