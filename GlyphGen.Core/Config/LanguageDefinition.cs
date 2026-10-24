using System.Text.Json.Serialization;

namespace GlyphGen.Core.Config;

/// <summary>The size of each rendered glyph image and the empty border around the drawing.</summary>
public sealed class CanvasConfig
{
    /// <summary>Width and height in pixels of each rendered glyph image. Default 512 (at least 16).</summary>
    public int SizePx { get; set; } = 512;
    /// <summary>Empty border in pixels kept around the drawing inside the image. Default 32.</summary>
    public int MarginPx { get; set; } = 32;
}

/// <summary>One glyph to generate for a language. Written in JSON as just its name (see
/// GlyphDefinitionConverter). Deliberately minimal for now — extend with per-glyph overrides
/// later if a specific glyph needs to deviate from its language's defaults.</summary>
[JsonConverter(typeof(GlyphDefinitionConverter))]
public sealed class GlyphDefinition
{
    public string Name { get; set; } = "";

}

/// <summary>One language: its glyph lists, shapes, recipes, derivations, quality checks and rendering.</summary>
public sealed class LanguageDefinition
{
    /// <summary>The language's name. Its random seed is derived from it, so renaming a language changes every glyph.</summary>
    public string Name { get; set; } = "";
    /// <summary>Size of each rendered glyph image and its margin.</summary>
    public CanvasConfig Canvas { get; set; } = new();

    /// <summary>The lattice this language's glyphs are drawn on. Required per language.</summary>
    public GridConfig Grid { get; set; } = new();

    /// <summary>The language's real glyphs - these end up in the output atlas/contact sheet.</summary>
    public List<GlyphDefinition> Glyphs { get; set; } = new();

    /// <summary>Glyphs that exist only to be components of combination recipes. Generated (before
    /// Glyphs, so recipes can use them) and rendered, but written to a separate "_bases" folder
    /// and left out of the contact sheet. They go through the same quality/similarity gate as
    /// every other glyph.</summary>
    public List<GlyphDefinition> BaseGlyphs { get; set; } = new();

    /// <summary>Glyphs used in more recipes roll lower counts/lengths, for shapes that have a
    /// count (guaranteed shapes are unaffected).</summary>
    public UsageSizeBiasConfig UsageSizeBias { get; set; } = new();

    /// <summary>What every plain (non-composite) glyph is built from, placed in list order: each
    /// entry carries its own chain rules, placement and arrangement. A guaranteed motif is an
    /// entry without a count; free-form strokes are entries with one.</summary>
    public List<ShapeDefinition> Shapes { get; set; } = new();

    /// <summary>Curve settings that are not per edge: edges no rule made, the proximity guard, and curves in composites.</summary>
    public CurveConfig Curve { get; set; } = new();
    /// <summary>How composite glyphs are built from components: recipes, scale and rotation, and the arrangement rules.</summary>
    public CombinationConfig Combination { get; set; } = new();

    /// <summary>Glyphs made by editing another glyph, by glyph name. A name listed here is not generated
    /// from shapes and has no recipe. Each entry has a `from` glyph and a list of `steps`.</summary>
    public Dictionary<string, DerivationConfig> Derivations { get; set; } = new();

    /// <summary>Post-generation gates (similarity to other glyphs, straight-line rejection).</summary>
    public QualityConfig Quality { get; set; } = new();

    /// <summary>Rendering (framing, colours, typeface). Pixel size/margin come from Canvas.</summary>
    public RenderConfig Render { get; set; } = new();
}

/// <summary>The lattice a language is drawn on: nodes lie at whole coordinates from 0 to width-1 and height-1.</summary>
public sealed class GridConfig
{
    /// <summary>Number of lattice columns (nodes along x). Default 25.</summary>
    public int Width { get; set; } = 25;
    /// <summary>Number of lattice rows (nodes along y). Default 25.</summary>
    public int Height { get; set; } = 25;
}

/// <summary>Top-level config for one full run — deserialized directly from the JSON file.</summary>
public sealed class RootConfig
{
    /// <summary>Sole source of true randomness for the entire run; everything else is derived
    /// deterministically from this via SeedTree.</summary>
    public ulong RootSeed { get; set; }

    public string OutputDirectory { get; set; } = "./output";

    /// <summary>How many threads generation may use. 1 (default) = one thread. 0 = every core. The result is identical whatever the
    /// number: threads only try quality retries of a plain/base glyph ahead of time, and the first attempt that passes in attempt
    /// order is the one kept.</summary>
    public int MaxParallelism { get; set; } = 1;

    public List<LanguageDefinition> Languages { get; set; } = new();

    /// <summary>Options the loader found that have no effect (set while the mode that uses them is off). Filled by the loader,
    /// printed with the run's warnings; never part of the json.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> Warnings { get; } = new();
}