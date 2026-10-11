using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// The side preference shared by shapes (SidePull) and combination components (placement rules):
/// choosing the sides - listed, or drawn at random - and scoring a placement by how near it is to them.
/// </summary>
public static class SideScoring
{
    /// <summary>The sides to use: the listed ones, or for SetRandom a draw from rng according to pick. The draw
    /// only uses the rng it is given, so pass a derived stream to leave every other random sequence alone.</summary>
    public static List<GlyphSide> Choose(bool random, IReadOnlyList<GlyphSide> listed, RandomSidePick? pick, DeterministicRandom rng)
    {
        if (!random) return listed.ToList();

        GlyphSide Horizontal() => rng.NextInt(0, 2) == 0 ? GlyphSide.Left : GlyphSide.Right;
        GlyphSide Vertical() => rng.NextInt(0, 2) == 0 ? GlyphSide.Top : GlyphSide.Bottom;

        return (pick ?? RandomSidePick.Any) switch
        {
            RandomSidePick.Horizontal => new List<GlyphSide> { Horizontal() },
            RandomSidePick.Vertical => new List<GlyphSide> { Vertical() },
            RandomSidePick.Corner => new List<GlyphSide> { Horizontal(), Vertical() },
            _ => new List<GlyphSide> { rng.NextInt(0, 4) switch
            {
                0 => GlyphSide.Left,
                1 => GlyphSide.Right,
                2 => GlyphSide.Top,
                _ => GlyphSide.Bottom,
            } },
        };
    }

    /// <summary>Lower is better: 0 at the preferred edge(s) of the grid and 1 per side at the opposite edge,
    /// times weight. centerX / centerY is the middle of the placed shape's bounding box. Top is the smaller y:
    /// row 0 is the top of the image.</summary>
    public static double Score(IReadOnlyList<GlyphSide> sides, double weight, double centerX, double centerY, LatticeGrid grid)
    {
        double fx = centerX / Math.Max(1, grid.Width - 1);
        double fy = centerY / Math.Max(1, grid.Height - 1);

        double score = 0.0;
        foreach (var side in sides)
        {
            score += side switch
            {
                GlyphSide.Left => fx,
                GlyphSide.Right => 1.0 - fx,
                GlyphSide.Top => fy,
                GlyphSide.Bottom => 1.0 - fy,
                _ => 0.0,
            };
        }

        return score * weight;
    }
}
