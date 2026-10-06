using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Checks a candidate shape's spacing against every already-placed shape, per
/// SpacingConfig. The single place this logic lives: GlyphGenerator uses it for
/// motifs and free-form sub-symbols, CombinationResolver for whole placed components.
///
/// Called AFTER a candidate is fully built - the checks need the complete node set (a centroid
/// can't be computed from a partial shape), so this is a post-generation validation. A failure
/// is treated exactly like a collision: the caller discards that attempt and tries another.
/// </summary>
public static class SubSymbolSpacingChecker
{
    /// <summary>Convenience for a shape made of several chains (a combination component):
    /// all the chains' nodes are pooled and measured as one shape.</summary>
    public static bool SatisfiesSpacingAsOneShape(
        IEnumerable<SubSymbolChain> candidateChains,
        IReadOnlyList<IReadOnlyList<GridPoint>> existingGroups,
        SpacingConfig config)
    {
        if (!config.Enabled) return true;
        return SatisfiesSpacing(candidateChains.SelectMany(c => c.Nodes).ToList(), existingGroups, config);
    }

    public static bool SatisfiesSpacing(
        IReadOnlyList<GridPoint> candidateNodes,
        IReadOnlyList<IReadOnlyList<GridPoint>> existingGroups,
        SpacingConfig config)
    {
        if (!config.Enabled) return true;
        if (candidateNodes.Count == 0) return true;

        const double eps = 1e-9;
        bool needClosest = config.MinNodeDistance > 0 || config.MaxNodeDistance > 0;
        bool anyGroupSeen = false;
        bool nearAnyGroup = false;
        (double X, double Y)? candidateCentroid = null;

        foreach (var group in existingGroups)
        {
            if (group.Count == 0) continue;
            anyGroupSeen = true;

            if (config.MinCenterDistance > 0)
            {
                candidateCentroid ??= Centroid(candidateNodes);
                if (Distance(candidateCentroid.Value, Centroid(group)) < config.MinCenterDistance)
                    return false;
            }

            if (needClosest)
            {
                double d = ClosestPairDistance(candidateNodes, group);

                if (config.MinNodeDistance > 0 && d < config.MinNodeDistance)
                    return false;

                if (config.MaxNodeDistance > 0)
                {
                    bool within = d <= config.MaxNodeDistance + eps;
                    if (within) nearAnyGroup = true;
                    else if (config.MaxDistanceMode == ProximityMode.AllShapes) return false;
                }
            }
        }

        // The first shape has nothing to be near, so the NearestShape rule only applies once
        // at least one other shape exists.
        if (config.MaxNodeDistance > 0
            && config.MaxDistanceMode == ProximityMode.NearestShape
            && anyGroupSeen && !nearAnyGroup)
            return false;

        return true;
    }

    private static (double X, double Y) Centroid(IReadOnlyList<GridPoint> nodes)
    {
        double sx = 0, sy = 0;
        foreach (var n in nodes) { sx += n.X; sy += n.Y; }
        return (sx / nodes.Count, sy / nodes.Count);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Brute-force O(n*m); fine given the small node counts (tens, not thousands)
    /// involved per shape.</summary>
    private static double ClosestPairDistance(IReadOnlyList<GridPoint> a, IReadOnlyList<GridPoint> b)
    {
        double best = double.MaxValue;
        foreach (var pa in a)
            foreach (var pb in b)
            {
                double dx = pa.X - pb.X, dy = pa.Y - pb.Y;
                double d = dx * dx + dy * dy; // compare squared distances, sqrt once at the end
                if (d < best) best = d;
            }
        return Math.Sqrt(best);
    }
}
