namespace GlyphGen.Core.Config;

/// <summary>
/// Post-generation quality gates applied to every finished glyph in a language, checked
/// against every glyph already accepted earlier in the same run. A glyph failing either check
/// is regenerated (fresh seed) up to MaxRegenerationAttempts times before the run fails with a
/// clear error naming the glyph.
/// </summary>
public sealed class QualityConfig
{
    /// <summary>Jaccard similarity (0-1) of two glyphs' normalized shapes, at or above which
    /// they're considered too similar and one gets regenerated. 1.0 (or higher) disables the
    /// check entirely. Default 0.7 matches "no more than 70% alike".</summary>
    public double MaxSimilarity { get; set; } = 0.7;

    /// <summary>Reject a glyph whose entire geometry (across all its chains/components)
    /// reduces to a single straight line with no bends.</summary>
    public bool RejectStraightLineOnly { get; set; } = true;

    public int MaxRegenerationAttempts { get; set; } = 10;
}
