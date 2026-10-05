using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Post-generation quality gates: rejects a glyph that is too visually similar to one already
/// accepted earlier in the same language, or whose entire geometry is a single straight line.
///
/// Similarity is the Jaccard index (intersection over union) of each glyph's node set after
/// normalizing away translation (shifting so its bounding box's minimum corner sits at the
/// origin) - a simple, cheap, deterministic shape fingerprint. It is NOT rotation-, mirror- or
/// scale-invariant: two glyphs that are the same shape rotated/mirrored/resized will not be
/// flagged as similar. That stronger notion is a meaningfully bigger comparison (trying every
/// transform of one shape against the other); flag if it's what's actually wanted.
/// </summary>
public static class GlyphQualityChecker
{
    public static bool IsTooSimilar(Glyph candidate, IEnumerable<Glyph> alreadyAccepted, double maxSimilarity)
    {
        if (maxSimilarity >= 1.0) return false; // check disabled

        var candidateShape = NormalizedShape(candidate);
        foreach (var other in alreadyAccepted)
        {
            if (JaccardSimilarity(candidateShape, NormalizedShape(other)) >= maxSimilarity)
                return true;
        }

        return false;
    }

    /// <summary>True when every node of the whole glyph lies on one single straight line (or
    /// there are too few distinct points to form anything else) - i.e. the glyph is visually
    /// just one stroke, however many nodes/chains it was generated from.</summary>
    public static bool IsTrivialStraightLine(Glyph glyph)
    {
        var points = GlyphGeometryUtils.GetAllNodes(glyph).Distinct().ToList();
        if (points.Count <= 2) return true; // degenerate, or literally a single edge

        var a = points[0];

        GridPoint? second = null;
        foreach (var p in points.Skip(1))
        {
            if (!p.Equals(a)) { second = p; break; }
        }
        if (second is null) return true; // every point identical - degenerate

        var b = second.Value;
        foreach (var p in points)
        {
            long cross = (long)(b.Y - a.Y) * (p.X - b.X) - (long)(b.X - a.X) * (p.Y - b.Y);
            if (cross != 0) return false; // a point off the a-b line: not a straight line
        }

        return true;
    }

    private static HashSet<(int X, int Y)> NormalizedShape(Glyph glyph)
    {
        var nodes = GlyphGeometryUtils.GetAllNodes(glyph).ToList();
        if (nodes.Count == 0) return new HashSet<(int X, int Y)>();

        int minX = nodes.Min(p => p.X), minY = nodes.Min(p => p.Y);
        return nodes.Select(p => (X: p.X - minX, Y: p.Y - minY)).ToHashSet();
    }

    private static double JaccardSimilarity(HashSet<(int X, int Y)> a, HashSet<(int X, int Y)> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;

        var intersection = new HashSet<(int X, int Y)>(a);
        intersection.IntersectWith(b);
        var union = new HashSet<(int X, int Y)>(a);
        union.UnionWith(b);

        return union.Count == 0 ? 0.0 : (double)intersection.Count / union.Count;
    }
}
