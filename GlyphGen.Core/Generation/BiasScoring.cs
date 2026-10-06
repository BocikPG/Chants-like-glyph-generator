using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Generation;

/// <summary>
/// The one implementation of bias scoring, shared by shape placement ("best of N" candidates) and
/// combination (ranking component placements). Lower is better. The score is that of the glyph's
/// bounding box - "everything already placed + the candidate".
/// </summary>
public static class BiasScoring
{
    /// <summary>Each active mode contributes a term on a 0..1 scale (extents are divided by the
    /// grid's extent, squareness is already a ratio) and the terms are summed, each multiplied by
    /// its configured weight (1 by default).</summary>
    public static double Score(CombinationBiasConfig bias, int width, int height, LatticeGrid grid)
    {
        double w = (double)width / Math.Max(1, grid.Width - 1);
        double h = (double)height / Math.Max(1, grid.Height - 1);

        double score = 0.0;
        foreach (var mode in bias.Modes)
        {
            score += bias.WeightOf(mode) * mode switch
            {
                CombinationBiasMode.Tallest => -h,
                CombinationBiasMode.Shortest => h,
                CombinationBiasMode.Widest => -w,
                CombinationBiasMode.Narrowest => w,
                CombinationBiasMode.Squarest => Math.Max(width, height) > 0
                    ? 1.0 - (double)Math.Min(width, height) / Math.Max(width, height)
                    : 0.0,
                _ => 0.0,
            };
        }

        return score;
    }

    public static (int MinX, int MaxX, int MinY, int MaxY)? BoundsOfGroups(
        IReadOnlyList<IReadOnlyList<GridPoint>> groups)
    {
        bool any = false;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

        foreach (var group in groups)
            foreach (var p in group)
            {
                any = true;
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }

        return any ? (minX, maxX, minY, maxY) : null;
    }

    /// <summary>Score of the glyph's bounding box with a candidate's nodes added to what is placed.</summary>
    public static double ScoreWithCandidate(
        CombinationBiasConfig bias, IReadOnlyList<IReadOnlyList<GridPoint>> placed,
        IReadOnlyList<GridPoint> candidate, LatticeGrid grid)
    {
        var all = new List<IReadOnlyList<GridPoint>>(placed) { candidate };
        var b = BoundsOfGroups(all);
        return b is { } e ? Score(bias, e.MaxX - e.MinX, e.MaxY - e.MinY, grid) : 0.0;
    }
}
