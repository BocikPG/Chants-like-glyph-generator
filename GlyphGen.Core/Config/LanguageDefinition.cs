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

    public IntRange SubSymbolCount { get; set; } = new() { Min = 1, Max = 1 };
    public IntRange SubSymbolLength { get; set; } = new() { Min = 3, Max = 6 };

    /// <summary>Optional: glyphs used in more recipes roll lower sub-symbol counts/lengths.</summary>
    public UsageSizeBiasConfig UsageSizeBias { get; set; } = new();

    public List<ConnectionRuleConfig> ConnectionRules { get; set; } = new();

    public bool AllowNodeRevisit { get; set; } = false;
    public bool AllowEdgeRevisit { get; set; } = false;
    public bool AllowClosedLoop { get; set; } = false;
    public double ClosedLoopWeight { get; set; } = 0.0;

    /// <summary>If false, the intersection checker rejects any candidate edge that crosses an
    /// already-committed edge, elsewhere in this same sub-symbol or any other sub-symbol/motif
    /// already placed in the glyph.</summary>
    public bool AllowSymbolsToCross { get; set; } = false;

    public List<GuaranteedMotifConfig> GuaranteedMotifs { get; set; } = new();

    /// <summary>Shared PlacementConfig schema, applied to regular free-form sub-symbols
    /// (guaranteed motifs carry their own placement block instead).</summary>
    public PlacementConfig SubSymbolPlacement { get; set; } = new();

    public CurveConfig Curve { get; set; } = new();
    public CombinationConfig Combination { get; set; } = new();

    /// <summary>Post-generation gates (similarity to other glyphs, straight-line rejection).</summary>
    public QualityConfig Quality { get; set; } = new();

    /// <summary>Rendering (framing, colours, typeface). Pixel size/margin come from Canvas.</summary>
    public RenderConfig Render { get; set; } = new();

    /// <summary>Minimum spacing between sub-symbols/motifs, beyond just not crossing.</summary>
    public SubSymbolSpacingConfig SubSymbolSpacing { get; set; } = new();
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
    public List<LanguageDefinition> Languages { get; set; } = new();
}
