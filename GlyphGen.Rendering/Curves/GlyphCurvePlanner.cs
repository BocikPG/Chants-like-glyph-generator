using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;
using GlyphGen.Core.Geometry;

namespace GlyphGen.Rendering.Curves;

/// <summary>
/// Gives every glyph its drawable plan from the curves stored in the glyph (GridEdge.Bend), and applies
/// CurveContextMode: in a context that is meant to be straight the stored curves are flattened to lines.
/// </summary>
public sealed class GlyphCurvePlanner
{
    private readonly Dictionary<string, PlannedGlyph> _natural = new();
    private readonly bool _miniCurved;
    private readonly bool _combinedCurved;

    public GlyphCurvePlanner(IEnumerable<Glyph> glyphs, CurveConfig curve, ulong languageSeed)
    {
        (_miniCurved, _combinedCurved) = curve.ContextMode switch
        {
            CurveContextMode.AllCurved => (true, true),
            CurveContextMode.AllStraight => (false, false),
            CurveContextMode.CurvedMiniStraightCombined => (true, false),
            CurveContextMode.StraightMiniCurvedCombined => (false, true),
            _ => throw new NotSupportedException($"Unknown curve context mode: {curve.ContextMode}"),
        };
    }

    /// <summary>The plan to draw for this glyph: "mini" rules for a plain glyph, "combined"
    /// rules for a composite.</summary>
    public PlannedGlyph Plan(Glyph glyph)
    {
        var natural = GetNatural(glyph);
        bool curved = glyph.Components.Count > 0 ? _combinedCurved : _miniCurved;
        return curved ? natural : Straightened(natural);
    }

    private PlannedGlyph GetNatural(Glyph glyph)
    {
        if (_natural.TryGetValue(glyph.Name, out var cached)) return cached;
        var result = CurvePlanner.Plan(glyph);
        _natural[glyph.Name] = result;
        return result;
    }

    private static PlannedGlyph Straightened(PlannedGlyph source)
    {
        var result = new PlannedGlyph { Name = source.Name };
        foreach (var layer in source.Layers)
        {
            var newLayer = new PlannedLayer { Label = layer.Label };
            foreach (var chain in layer.Chains)
            {
                var newChain = new PlannedChain { Closed = chain.Closed, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag, Kind = chain.Kind };
                foreach (var e in chain.Edges)
                    newChain.Edges.Add(chain.Kind != PlannedChainKind.Stroke
                        ? e // circles and dots are not "bent lines": flattening would wreck them
                        : new PlannedEdge { P0 = e.P0, C1 = e.P0, C2 = e.P3, P3 = e.P3, IsLine = true });
                newLayer.Chains.Add(newChain);
            }
            result.Layers.Add(newLayer);
        }
        return result;
    }
}
