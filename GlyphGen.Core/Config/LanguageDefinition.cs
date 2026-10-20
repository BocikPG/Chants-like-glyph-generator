using System.Text.Json.Serialization;

namespace GlyphGen.Core.Config;

public sealed class CanvasConfig
{
    public int SizePx { get; set; } = 512;
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

public sealed class LanguageDefinition
{
    public string Name { get; set; } = "";
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

    public CurveConfig Curve { get; set; } = new();
    public CombinationConfig Combination { get; set; } = new();

    /// <summary>Glyphs made by editing another glyph, by glyph name. A name listed here is not generated
    /// from shapes and has no recipe. See DerivationConfig.</summary>
    public Dictionary<string, DerivationConfig> Derivations { get; set; } = new();

    /// <summary>Post-generation gates (similarity to other glyphs, straight-line rejection).</summary>
    public QualityConfig Quality { get; set; } = new();

    /// <summary>Rendering (framing, colours, typeface). Pixel size/margin come from Canvas.</summary>
    public RenderConfig Render { get; set; } = new();
}

public sealed class GridConfig
{
    public int Width { get; set; } = 25;
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
}