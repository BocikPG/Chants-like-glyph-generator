using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>Where the nodes go when a straight edge is cut into pieces: the SplitEdges derivation step and a connection rule's
/// <c>split</c> block both use this, so they place nodes the same way.</summary>
internal static class EdgeSplitting
{
    /// <summary>The nodes that cut an edge into <paramref name="pieces"/> pieces, in order from its start to its end. Fewer (or none)
    /// when the edge offers no free place for them.</summary>
    public static List<GridPoint> PickPoints(GridEdge edge, int pieces, SplitDistribution distribution, SplitSnap snap, double maxDeviation,
        HashSet<GridPoint> occupied, LatticeGrid grid, DeterministicRandom rng)
    {
        int dx = edge.To.X - edge.From.X, dy = edge.To.Y - edge.From.Y;
        int wanted = pieces - 1;
        var result = new List<GridPoint>();

        if (snap == SplitSnap.Exact)
        {
            int g = Gcd(Math.Abs(dx), Math.Abs(dy));
            if (g < 2) return result;

            var free = new List<int>();
            for (int j = 1; j < g; j++)
                if (!occupied.Contains(new GridPoint(edge.From.X + dx / g * j, edge.From.Y + dy / g * j))) free.Add(j);

            var chosen = new List<int>();
            if (free.Count <= wanted) chosen.AddRange(free);
            else if (distribution == SplitDistribution.Random)
            {
                Shuffle(free, rng);
                chosen.AddRange(free.Take(wanted));
            }
            else
            {
                for (int i = 1; i <= wanted; i++)
                {
                    double ideal = (double)i / pieces;
                    int best = free.Where(j => !chosen.Contains(j)).OrderBy(j => Math.Abs((double)j / g - ideal)).ThenBy(j => j).First();
                    chosen.Add(best);
                }
            }

            foreach (int j in chosen.OrderBy(j => j))
                result.Add(new GridPoint(edge.From.X + dx / g * j, edge.From.Y + dy / g * j));
            return result;
        }

        var fractions = new List<double>();
        if (distribution == SplitDistribution.Equal)
            for (int i = 1; i <= wanted; i++) fractions.Add((double)i / pieces);
        else
        {
            for (int i = 0; i < wanted; i++) fractions.Add(rng.NextDouble());
            fractions.Sort();
        }

        double length = Math.Sqrt((double)dx * dx + (double)dy * dy);
        var seen = new HashSet<GridPoint>();
        var found = new List<(GridPoint Point, double T)>();
        foreach (double f in fractions)
        {
            var p = new GridPoint((int)Math.Round(edge.From.X + f * dx), (int)Math.Round(edge.From.Y + f * dy));
            if (p.Equals(edge.From) || p.Equals(edge.To) || occupied.Contains(p) || !grid.Contains(p) || !seen.Add(p)) continue;

            double px = p.X - edge.From.X, py = p.Y - edge.From.Y;
            double off = Math.Abs(px * dy - py * dx) / length;
            if (off > maxDeviation + 1e-9) continue;
            found.Add((p, (px * dx + py * dy) / (length * length)));
        }

        return found.OrderBy(x => x.T).Select(x => x.Point).ToList();
    }

    private static void Shuffle<T>(IList<T> list, DeterministicRandom rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
