using System;

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
    /// component here. Each value is a list of component names, or an object with
    /// "components" and an optional per-recipe "arrangement".</summary>
    public Dictionary<string, RecipeConfig> Recipes { get; set; } = new();

    public bool AllowScale { get; set; } = false;

    /// <summary>Discrete, lattice-validated factors only — see ScaleFactorConfig for why a
    /// continuous float range isn't safe here.</summary>
    public List<ScaleFactorConfig> AllowedScales { get; set; } = new();

    public bool AllowRotate { get; set; } = false;
    public RotationConfig Rotation { get; set; } = new();

    public int MaxRetriesPerRecipe { get; set; } = 5;

    /// <summary>How a composite's components relate to each other: crossing (default: none -
    /// overlay composition is controlled stacking, they may only touch at shared nodes), spacing
    /// and bias. A recipe can override parts of it with its own "arrangement". Each placed
    /// component counts as one shape. Components search every translation exhaustively, so
    /// arrangement.candidates does not apply here.</summary>
    public ArrangementRules Arrangement { get; set; } = new();
}
