namespace GlyphGen.Core.Config;

/// <summary>
/// Overlay-composition rules for combined glyphs (e.g. Anchorite-style stacking). A composite
/// glyph's components are generated independently first, then translated (and optionally
/// scaled/rotated) onto the composite's own grid. On placement failure, the least-used
/// dependency (via GlyphUsageTracker) is regenerated first, escalating through the recipe's
/// components up to MaxRetriesPerRecipe before the run fails with a named error.
/// </summary>
public sealed class CombinationConfig
{
    /// <summary>Key = composite glyph name, value = component glyph names to overlay. A glyph
    /// name may appear both as a standalone entry in the language's glyph list and as a
    /// component here.</summary>
    public Dictionary<string, List<string>> Recipes { get; set; } = new();

    public bool AllowScale { get; set; } = false;

    /// <summary>Discrete, lattice-validated factors only — see ScaleFactorConfig for why a
    /// continuous float range isn't safe here.</summary>
    public List<ScaleFactorConfig> AllowedScales { get; set; } = new();

    public bool AllowRotate { get; set; } = false;
    public RotationConfig Rotation { get; set; } = new();

    public int MaxRetriesPerRecipe { get; set; } = 5;

    /// <summary>If true, a composite's components may cross each other (they may always touch at
    /// shared nodes). Default false: overlay composition is controlled stacking, with no component
    /// crossing another, regardless of the language's AllowSymbolsToCross (which governs strokes
    /// inside one glyph). SubSymbolSpacing still applies either way.</summary>
    public bool AllowComponentsToCross { get; set; } = false;
    public CombinationBiasConfig Bias { get; set; } = new();
}
