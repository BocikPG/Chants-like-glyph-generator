using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Shared helpers for reading a glyph's geometry regardless of whether it's standalone (its
/// own Chains) or composite (its Components' already-transformed chains). Used by both overlay
/// composition and the post-generation quality checks.
/// </summary>
public static class GlyphGeometryUtils
{
    public static IEnumerable<SubSymbolChain> GetAllChains(Glyph glyph)
    {
        foreach (var chain in glyph.Chains) yield return chain;
        foreach (var component in glyph.Components)
            foreach (var chain in component.TransformedChains) yield return chain;
    }

    public static IEnumerable<GridPoint> GetAllNodes(Glyph glyph) =>
        GetAllChains(glyph).SelectMany(c => c.Nodes);
}
