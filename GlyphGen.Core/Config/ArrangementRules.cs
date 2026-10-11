namespace GlyphGen.Core.Config;

/// <summary>
/// How a shape relates to the shapes already placed in the glyph: whether it may cross them,
/// how far from them it must (or must not) sit, and which of several legal placements is
/// preferred. Used by every entry in a language's shapes list and by the combination block
/// (for the components of a composite).
/// </summary>
public class ArrangementRules
{
    /// <summary>If true, this shape's edges may cross edges already placed. A crossing is only
    /// allowed when BOTH shapes allow it: the shape being placed AND the shape that owns the
    /// edge it would cross. So a shape with allowCrossing false can never be crossed, even by a
    /// shape that is itself allowed to cross. Edges that merely meet at a shared node never count
    /// as crossing.</summary>
    public bool AllowCrossing { get; set; } = false;

    /// <summary>Distances to keep from the shapes already placed (centroid and closest-node
    /// minimums/maximums). The rules of the shape being placed are checked against everything
    /// already there, so the outcome depends on placement order.</summary>
    public SpacingConfig Spacing { get; set; } = new();

    /// <summary>Preference among placements that already satisfy every hard rule (grid bounds,
    /// crossing, spacing). Never relaxes a rule. Empty modes = no bias.</summary>
    public CombinationBiasConfig Bias { get; set; } = new();

    /// <summary>Shapes only: how many valid candidates to generate when a bias is active; the
    /// one whose glyph bounding box scores best is kept ("best of N"). Ignored without a bias,
    /// and by combination components (they search every placement exhaustively).</summary>
    public int Candidates { get; set; } = 3;
}
