namespace GlyphGen.Core.Grid;

/// <summary>
/// Curve-aware crossing tests between edges. Two straight edges (or a straight edge and a circle) keep the exact
/// integer test of <see cref="GridEdge.Intersects"/>; as soon as a planned bend is involved the edges are compared as
/// polylines sampled finely enough for the lattice sizes used here. Edges that only meet at a shared end point never cross.
/// </summary>
public static class EdgePaths
{
    private const int CurveSegments = 24;
    private const int CircleSegments = 48;

    /// <summary>Distance from a shared end point inside which a polyline meeting is just the edges touching there.</summary>
    private const double TouchRadius = 0.06;

    public static (double X, double Y)[] Polyline(GridEdge e)
    {
        if (e.IsDot) return new[] { ((double)e.From.X, (double)e.From.Y) };
        if (e.IsCircle)
        {
            var (cx, cy, r) = e.CircleGeometry;
            var pts = new (double X, double Y)[CircleSegments + 1];
            for (int i = 0; i <= CircleSegments; i++)
            {
                double a = 2 * Math.PI * i / CircleSegments;
                pts[i] = (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
            }
            return pts;
        }
        if (e.IsCurved) return e.Bend!.Flatten(e.From, e.To, CurveSegments);
        return new[] { ((double)e.From.X, (double)e.From.Y), ((double)e.To.X, (double)e.To.Y) };
    }

    /// <summary>True when the two edges cut through each other (touching at a shared node does not count).</summary>
    public static bool Crosses(GridEdge a, GridEdge b)
    {
        if (!a.IsCurved && !b.IsCurved) return a.Intersects(b);
        if (a.IsDot || b.IsDot) return false;
        return PolylineCrossings(a, b, firstOnly: true).Count > 0;
    }

    /// <summary>Where the two edges cross (empty when they do not). Used to decide whether a crossing is near a node.</summary>
    public static List<(double X, double Y)> CrossingPoints(GridEdge a, GridEdge b)
    {
        var result = new List<(double X, double Y)>();
        if (!Crosses(a, b)) return result;

        result = PolylineCrossings(a, b, firstOnly: false);
        if (result.Count > 0) return result;

        // Exact test says they cross but no single meeting point exists (an overlap, or a circle cut through its
        // inside): use the end points lying on the other edge, else the middle of this edge.
        foreach (var p in new[] { a.From, a.To })
            if (DistanceToPath((p.X, p.Y), Polyline(b)) < 1e-6) result.Add((p.X, p.Y));
        foreach (var p in new[] { b.From, b.To })
            if (DistanceToPath((p.X, p.Y), Polyline(a)) < 1e-6) result.Add((p.X, p.Y));
        if (result.Count == 0) result.Add(((a.From.X + a.To.X) / 2.0, (a.From.Y + a.To.Y) / 2.0));
        return result;
    }

    private static List<(double X, double Y)> PolylineCrossings(GridEdge a, GridEdge b, bool firstOnly)
    {
        var result = new List<(double X, double Y)>();
        var pa = Polyline(a);
        var pb = Polyline(b);
        if (pa.Length < 2 || pb.Length < 2) return result;
        if (!BoxesOverlap(pa, pb)) return result;

        var shared = new List<(double X, double Y)>();
        foreach (var p in new[] { a.From, a.To })
            if (p == b.From || p == b.To) shared.Add((p.X, p.Y));

        for (int i = 0; i + 1 < pa.Length; i++)
            for (int j = 0; j + 1 < pb.Length; j++)
            {
                if (!SegmentCross(pa[i], pa[i + 1], pb[j], pb[j + 1], out var hit)) continue;
                bool atShared = false;
                foreach (var s in shared)
                    if (Math.Abs(hit.X - s.X) <= TouchRadius && Math.Abs(hit.Y - s.Y) <= TouchRadius) { atShared = true; break; }
                if (atShared) continue;
                result.Add(hit);
                if (firstOnly) return result;
            }
        return result;
    }

    private static bool BoxesOverlap((double X, double Y)[] a, (double X, double Y)[] b)
    {
        double aMinX = a.Min(p => p.X), aMaxX = a.Max(p => p.X), aMinY = a.Min(p => p.Y), aMaxY = a.Max(p => p.Y);
        double bMinX = b.Min(p => p.X), bMaxX = b.Max(p => p.X), bMinY = b.Min(p => p.Y), bMaxY = b.Max(p => p.Y);
        return aMinX <= bMaxX + 1e-9 && bMinX <= aMaxX + 1e-9 && aMinY <= bMaxY + 1e-9 && bMinY <= aMaxY + 1e-9;
    }

    private static bool SegmentCross((double X, double Y) p, (double X, double Y) q, (double X, double Y) r, (double X, double Y) s, out (double X, double Y) hit)
    {
        hit = default;
        double rx = q.X - p.X, ry = q.Y - p.Y, sx = s.X - r.X, sy = s.Y - r.Y;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-12) return false; // parallel: overlaps are handled by the exact test for straight edges
        double t = ((r.X - p.X) * sy - (r.Y - p.Y) * sx) / denom;
        double u = ((r.X - p.X) * ry - (r.Y - p.Y) * rx) / denom;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        hit = (p.X + t * rx, p.Y + t * ry);
        return true;
    }

    public static double DistanceToPath((double X, double Y) p, (double X, double Y)[] path)
    {
        double best = double.MaxValue;
        if (path.Length == 1) return Math.Sqrt((p.X - path[0].X) * (p.X - path[0].X) + (p.Y - path[0].Y) * (p.Y - path[0].Y));
        for (int i = 0; i + 1 < path.Length; i++)
        {
            double vx = path[i + 1].X - path[i].X, vy = path[i + 1].Y - path[i].Y;
            double len2 = vx * vx + vy * vy;
            double t = len2 < 1e-15 ? 0 : Math.Clamp(((p.X - path[i].X) * vx + (p.Y - path[i].Y) * vy) / len2, 0, 1);
            double dx = p.X - (path[i].X + t * vx), dy = p.Y - (path[i].Y + t * vy);
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    /// <summary>True when any two edges of the chain cross each other (a chain may never cross itself). Exact retraces
    /// are skipped - allowEdgeRevisit decides those separately.</summary>
    public static bool ChainCrossesItself(IReadOnlyList<GridEdge> edges)
    {
        for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
            {
                var a = edges[i]; var b = edges[j];
                if (a.Kind == b.Kind && ((a.From == b.From && a.To == b.To) || (a.From == b.To && a.To == b.From))) continue;
                if (Crosses(a, b)) return true;
            }
        return false;
    }
}
