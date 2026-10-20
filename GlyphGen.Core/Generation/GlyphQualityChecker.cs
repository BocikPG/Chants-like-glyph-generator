using GlyphGen.Core.Config;
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
    public static bool IsTooSimilar(Glyph candidate, IEnumerable<Glyph> alreadyAccepted, double maxSimilarity) =>
        FindTooSimilar(candidate, alreadyAccepted, maxSimilarity) is not null;

    /// <summary>The already-accepted glyph the candidate resembles MOST (and by how much), if
    /// that reaches maxSimilarity - null when the candidate is distinct enough (or the check is
    /// disabled). Lets an error name the glyph it clashed with instead of just failing.</summary>
    public static (Glyph Other, double Similarity)? FindTooSimilar(
        Glyph candidate, IEnumerable<Glyph> alreadyAccepted, double maxSimilarity, QualityConfig? options = null)
    {
        if (maxSimilarity >= 1.0) return null; // check disabled

        var basis = options?.SimilarityBasis ?? SimilarityBasis.Nodes;
        bool position = options?.ComparePosition ?? false;
        var candidateShape = NormalizedShape(candidate, basis, position);
        (Glyph Other, double Similarity)? worst = null;
        foreach (var other in alreadyAccepted)
        {
            double similarity = JaccardSimilarity(candidateShape, NormalizedShape(other, basis, position));
            if (similarity >= maxSimilarity && (worst is null || similarity > worst.Value.Similarity))
                worst = (other, similarity);
        }

        return worst;
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

    private static HashSet<(int, int, int, int, int, int, int)> NormalizedShape(Glyph glyph, SimilarityBasis basis, bool comparePosition)
    {
        var nodes = GlyphGeometryUtils.GetAllNodes(glyph).ToList();
        var set = new HashSet<(int, int, int, int, int, int, int)>();
        if (nodes.Count == 0) return set;

        int minX = comparePosition ? 0 : nodes.Min(p => p.X), minY = comparePosition ? 0 : nodes.Min(p => p.Y);
        if (basis == SimilarityBasis.Nodes)
        {
            foreach (var p in nodes) set.Add((p.X - minX, p.Y - minY, -1, -1, -1, -1, -1));
            return set;
        }

        foreach (var chain in GlyphGeometryUtils.GetAllChains(glyph))
            foreach (var e in chain.Edges)
            {
                var a = (X: e.From.X - minX, Y: e.From.Y - minY);
                var b = (X: e.To.X - minX, Y: e.To.Y - minY);
                // An undirected line or circle is the same either way round; an arc's span belongs to its direction.
                if (!e.IsArc && (b.X < a.X || (b.X == a.X && b.Y < a.Y))) (a, b) = (b, a);
                set.Add((a.X, a.Y, b.X, b.Y, (int)e.Kind * 2 + (e.IsCurved ? 1 : 0), e.Arc?.RelStart ?? -1, e.Arc?.Sweep ?? -1));
            }
        return set;
    }

    private static double JaccardSimilarity(HashSet<(int, int, int, int, int, int, int)> a, HashSet<(int, int, int, int, int, int, int)> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;

        var intersection = new HashSet<(int, int, int, int, int, int, int)>(a);
        intersection.IntersectWith(b);
        var union = new HashSet<(int, int, int, int, int, int, int)>(a);
        union.UnionWith(b);

        return union.Count == 0 ? 0.0 : (double)intersection.Count / union.Count;
    }
}
