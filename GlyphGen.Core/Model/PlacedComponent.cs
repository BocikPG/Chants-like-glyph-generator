using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Model;

/// <summary>
/// Records how one dependency glyph was transformed when overlaid into a composite glyph
/// (translation always, scale/rotation only when the language's CombinationConfig allows it).
/// Needed both for rendering and so a failed placement can be traced back to exactly which
/// component/transform combination didn't fit.
/// </summary>
public sealed class PlacedComponent
{
    public string SourceGlyphName { get; set; } = "";
    public GridPoint Translation { get; set; }
    public int RotationDegrees { get; set; }
    public int ScaleNumerator { get; set; } = 1;
    public int ScaleDenominator { get; set; } = 1;

    /// <summary>The source glyph's chains, already transformed into this composite's
    /// coordinate space — what actually gets rendered/intersection-checked.</summary>
    public List<SubSymbolChain> TransformedChains { get; } = new();
}
