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
        if (e.IsArc)
        {
            int n = Math.Max(4, (int)Math.Ceiling(e.Arc!.Value.Sweep / 7.5));
            var pts = new (double X, double Y)[n + 1];
            for (int i = 0; i <= n; i++) pts[i] = e.CirclePoint(e.ArcStartDegrees + e.Arc.Value.Sweep * i / n);
            return pts;
        }
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
        // A dot is a point: it is crossed by an edge that passes through it (an edge that merely ends on it meets it at a node).
        if (a.IsDot || b.IsDot)
        {
            if (a.IsDot && b.IsDot) return false;
            var dot = a.IsDot ? a : b;
            var other = a.IsDot ? b : a;
            if (dot.From == other.From || dot.From == other.To) return false;
            return DistanceToPath((dot.From.X, dot.From.Y), Polyline(other)) < (other.IsCurved || other.IsCircle ? TouchRadius : 1e-9);
        }

        if (!a.IsCurved && !b.IsCurved && !a.IsArc && !b.IsArc) return a.Intersects(b);
        if (a.IsDot || b.IsDot) return false;
        return PolylineCrossings(a, b, firstOnly: true).Count > 0;
    }

    /// <summary>True when the edges have anything in common: they cross, or an end point (or extra node) of one lies on the other.
    /// Used by CrossingMode.Strict, which allows no contact at all.</summary>
    public static bool Touches(GridEdge a, GridEdge b)
    {
        if (Crosses(a, b)) return true;
        var pa = Polyline(a);
        var pb = Polyline(b);
        foreach (var p in PointsOf(a))
            if (DistanceToPath(p, pb) < TouchRadius) return true;
        foreach (var p in PointsOf(b))
            if (DistanceToPath(p, pa) < TouchRadius) return true;
        return false;
    }

    private static IEnumerable<(double X, double Y)> PointsOf(GridEdge e)
    {
        yield return (e.From.X, e.From.Y);
        yield return (e.To.X, e.To.Y);
        foreach (var n in e.ExtraNodes()) yield return (n.X, n.Y);
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

    /// <summary>Radius around a meeting point (shared node, allowed crossing) inside which TooClose is waived, as a
    /// multiple of the required distance. 1.5 lets strokes that meet at about 40 degrees or more pass.</summary>
    private const double MeetingRadiusFactor = 1.5;

    /// <summary>True when the paths of the two edges come closer than <paramref name="distance"/>, except near the places where
    /// they meet on purpose: a node of <paramref name="aNodes"/> (the chain 'a' belongs to) lying on b, an end point of b
    /// lying on a, or a crossing. Within 1.5 x distance of such a point the edges may be as close as they like.</summary>
    public static bool TooClose(GridEdge a, GridEdge b, double distance, IEnumerable<GridPoint>? aNodes)
    {
        if (distance <= 0) return false;
        return TooClose(a, Polyline(a), b, Polyline(b), distance, aNodes);
    }

    /// <summary>As <see cref="TooClose(GridEdge, GridEdge, double, IEnumerable{GridPoint}?)"/> with the edges' paths (see <see cref="Polyline"/>)
    /// already computed, for callers that test one edge against many.</summary>
    public static bool TooClose(GridEdge a, (double X, double Y)[] pa, GridEdge b, (double X, double Y)[] pb, double distance, IEnumerable<GridPoint>? aNodes)
    {
        if (distance <= 0) return false;
        if (!BoxesNear(pa, pb, distance)) return false;
        // Cheap test first: do the paths come closer than the distance anywhere, meeting points or not? Most pairs do not.
        if (!PathsCloserThan(pa, pb, distance)) return false;

        var meeting = new List<(double X, double Y)>();
        if (aNodes is not null)
            foreach (var n in aNodes)
                if (DistanceToPath((n.X, n.Y), pb) < 1e-6) meeting.Add((n.X, n.Y));
        foreach (var p in new[] { a.From, a.To })
            if (DistanceToPath((p.X, p.Y), pb) < 1e-6) meeting.Add((p.X, p.Y));
        foreach (var p in new[] { b.From, b.To })
            if (DistanceToPath((p.X, p.Y), pa) < 1e-6) meeting.Add((p.X, p.Y));

        double radius = MeetingRadiusFactor * distance;
        bool tooClose = AnyPointTooClose(pa, pb, distance, radius, meeting) || AnyPointTooClose(pb, pa, distance, radius, meeting);
        if (!tooClose || a.IsDot || b.IsDot) return tooClose;

        // Crossings are meeting points too (a crossing the shapes allow); only worth finding once something is still too close.
        var crossings = CrossingPoints(a, b);
        if (crossings.Count == 0) return true;
        meeting.AddRange(crossings);
        return AnyPointTooClose(pa, pb, distance, radius, meeting) || AnyPointTooClose(pb, pa, distance, radius, meeting);
    }

    private static bool AnyPointTooClose((double X, double Y)[] from, (double X, double Y)[] path, double distance, double radius,
        List<(double X, double Y)>? meeting)
    {
        foreach (var p in Dense(from, 0.2))
        {
            bool excused = false;
            if (meeting is not null)
            foreach (var m in meeting)
            {
                double dx = p.X - m.X, dy = p.Y - m.Y;
                if (dx * dx + dy * dy < radius * radius) { excused = true; break; }
            }
            if (excused) continue;
            if (DistanceToPath(p, path) < distance - 1e-6) return true;
        }
        return false;
    }

    private static IEnumerable<(double X, double Y)> Dense((double X, double Y)[] path, double step)
    {
        if (path.Length == 1) { yield return path[0]; yield break; }
        for (int i = 0; i + 1 < path.Length; i++)
        {
            double len = Math.Sqrt((path[i + 1].X - path[i].X) * (path[i + 1].X - path[i].X) + (path[i + 1].Y - path[i].Y) * (path[i + 1].Y - path[i].Y));
            int n = Math.Max(1, (int)Math.Ceiling(len / step));
            for (int k = 0; k < n; k++)
                yield return (path[i].X + (path[i + 1].X - path[i].X) * k / n, path[i].Y + (path[i + 1].Y - path[i].Y) * k / n);
        }
        yield return path[^1];
    }

    /// <summary>Exact test of whether two polylines come within <paramref name="distance"/> of each other anywhere.</summary>
    private static bool PathsCloserThan((double X, double Y)[] a, (double X, double Y)[] b, double distance)
    {
        double limit = distance - 1e-6;
        double limitSq = limit * limit;
        if (a.Length == 1 || b.Length == 1)
            return (a.Length == 1 ? DistanceToPath(a[0], b) : DistanceToPath(b[0], a)) < limit;
        for (int i = 0; i + 1 < a.Length; i++)
        {
            var p = a[i]; var q = a[i + 1];
            double aMinX = Math.Min(p.X, q.X) - limit, aMaxX = Math.Max(p.X, q.X) + limit;
            double aMinY = Math.Min(p.Y, q.Y) - limit, aMaxY = Math.Max(p.Y, q.Y) + limit;
            for (int j = 0; j + 1 < b.Length; j++)
            {
                var r = b[j]; var s = b[j + 1];
                if (Math.Max(r.X, s.X) < aMinX || Math.Min(r.X, s.X) > aMaxX || Math.Max(r.Y, s.Y) < aMinY || Math.Min(r.Y, s.Y) > aMaxY) continue;
                if (SegmentDistanceSq(p, q, r, s) < limitSq) return true;
            }
        }
        return false;
    }

    /// <summary>The smallest distance between two paths (polylines, see <see cref="Polyline"/>); 0 when they touch or cross.</summary>
    public static double PathDistance((double X, double Y)[] a, (double X, double Y)[] b)
    {
        if (a.Length == 1) return DistanceToPath(a[0], b);
        if (b.Length == 1) return DistanceToPath(b[0], a);
        double best = double.MaxValue;
        for (int i = 0; i + 1 < a.Length; i++)
            for (int j = 0; j + 1 < b.Length; j++)
            {
                best = Math.Min(best, SegmentDistanceSq(a[i], a[i + 1], b[j], b[j + 1]));
                if (best <= 0) return 0;
            }
        return Math.Sqrt(best);
    }

    private static double SegmentDistanceSq((double X, double Y) p, (double X, double Y) q, (double X, double Y) r, (double X, double Y) s)
    {
        if (SegmentCross(p, q, r, s, out _)) return 0;
        return Math.Min(Math.Min(PointSegmentSq(p, r, s), PointSegmentSq(q, r, s)), Math.Min(PointSegmentSq(r, p, q), PointSegmentSq(s, p, q)));
    }

    private static double PointSegmentSq((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, len2 = vx * vx + vy * vy;
        double t = len2 < 1e-15 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
        double dx = p.X - (a.X + t * vx), dy = p.Y - (a.Y + t * vy);
        return dx * dx + dy * dy;
    }

    /// <summary>Bounding box of a path, for rejecting far-apart edges cheaply.</summary>
    public readonly record struct PathBox(double MinX, double MinY, double MaxX, double MaxY)
    {
        public static PathBox Of((double X, double Y)[] path)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in path) { if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X; if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y; }
            return new PathBox(minX, minY, maxX, maxY);
        }

        public bool Near(PathBox o, double margin) =>
            MinX <= o.MaxX + margin && o.MinX <= MaxX + margin && MinY <= o.MaxY + margin && o.MinY <= MaxY + margin;
    }

    private static bool BoxesNear((double X, double Y)[] a, (double X, double Y)[] b, double margin)
    {
        double aMinX = double.MaxValue, aMaxX = double.MinValue, aMinY = double.MaxValue, aMaxY = double.MinValue;
        foreach (var p in a) { if (p.X < aMinX) aMinX = p.X; if (p.X > aMaxX) aMaxX = p.X; if (p.Y < aMinY) aMinY = p.Y; if (p.Y > aMaxY) aMaxY = p.Y; }
        double bMinX = double.MaxValue, bMaxX = double.MinValue, bMinY = double.MaxValue, bMaxY = double.MinValue;
        foreach (var p in b) { if (p.X < bMinX) bMinX = p.X; if (p.X > bMaxX) bMaxX = p.X; if (p.Y < bMinY) bMinY = p.Y; if (p.Y > bMaxY) bMaxY = p.Y; }
        return aMinX <= bMaxX + margin && bMinX <= aMaxX + margin && aMinY <= bMaxY + margin && bMinY <= aMaxY + margin;
    }

    /// <summary>True when two different edges of one chain come closer than <paramref name="distance"/> (see TooClose;
    /// the chain's own nodes are its meeting points). Exact retraces are skipped.</summary>
    public static bool ChainTooClose(IReadOnlyList<GridEdge> edges, IReadOnlyList<GridPoint> nodes, double distance)
    {
        if (distance <= 0) return false;
        for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
            {
                var a = edges[i]; var b = edges[j];
                if (a.Kind == b.Kind && ((a.From == b.From && a.To == b.To) || (a.From == b.To && a.To == b.From))) continue;
                if (TooClose(a, b, distance, nodes)) return true;
            }
        return false;
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
