using System;

namespace GlyphGen.Core.Config;

/// <summary>
/// Overlay-composition rules for combined glyphs (e.g. Anchorite-style stacking). A composite
/// glyph's components are generated independently first, then translated (and optionally
/// scaled/rotated) onto the composite's own grid. On placement failure, the least-used
/// dependency (the least-used glyph) is regenerated first, escalating through the recipe's
/// components up to MaxRetriesPerRecipe before the run fails with a named error.
/// </summary>
public sealed class CombinationConfig
{
    /// <summary>Key = composite glyph name, value = component glyph names to overlay. A glyph
    /// name may appear both as a standalone entry in the language's glyph list and as a
    /// component here. Each value is a list of component names, or an object with
    /// "components" and an optional per-recipe "arrangement".</summary>
    public Dictionary<string, RecipeConfig> Recipes { get; set; } = new();

    /// <summary>Whether a component may be scaled when it is placed in a composite. The ratios come from allowedScales. Default false.</summary>
    public bool AllowScale { get; set; } = false;

    /// <summary>Discrete ratios only (each an exact fraction such as 2/3): a shrinking ratio is only usable when every node of the component divides exactly, which a continuous range could not guarantee.</summary>
    public List<ScaleFactorConfig> AllowedScales { get; set; } = new();

    /// <summary>Whether a component may be rotated when it is placed in a composite. The angles come from rotation.allowed. Default false.</summary>
    public bool AllowRotate { get; set; } = false;
    public RotationConfig Rotation { get; set; } = new();

    /// <summary>How many times a composite is retried (regenerating its least-used components first) before the run fails with a named error. Default 100.</summary>
    public int Retries { get; set; } = 100;

    /// <summary>How a composite's components relate to each other: crossing (default: none -
    /// overlay composition is controlled stacking, they may only touch at shared nodes), spacing
    /// bias and per-component placement rules (placements). A recipe can override parts of it with its own "arrangement". Each placed
    /// component counts as one shape. Components search every translation exhaustively, so
    /// arrangement.candidates does not apply here.</summary>
    public CombinationArrangementRules Arrangement { get; set; } = new();

    /// <summary>Glyphs that are drawn AGAINST other glyphs: key = a base glyph, value = the glyphs (its partners) it is drawn together with.
    /// The partners are drawn first, in the same frame; their strokes are obstacles for every shape of the key glyph (under
    /// <c>arrangement.crossing</c> and <c>spacing.minEdgeDistance</c>), and a shape can start on their nodes
    /// (<c>placement.start.onPartnerNode</c>). A recipe that contains such a glyph is built as a plain overlay of its components at their own
    /// positions - no placement search, so no regenerating of components to make them fit. Such a recipe lists the glyph together with all its
    /// partners, and exactly one component without partners (the root) that everything else is drawn against. Redrawing a partner draws the
    /// glyphs that depend on it again. Default: none.</summary>
    public Dictionary<string, List<string>> Partners { get; set; } = new();

    /// <summary>With <c>partners</c>: a glyph drawn against partners has to touch every one of them (share a node with it, or cross one of its
    /// strokes where crossing is allowed); a draw that misses one is rejected and drawn again like any other quality failure. Default true.</summary>
    public bool PartnerContact { get; set; } = true;
}
