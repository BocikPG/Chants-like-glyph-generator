namespace GlyphGen.Core.Grid;

/// <summary>
/// A straight connection between two lattice points, possibly spanning several units
/// (per a ConnectionRuleConfig's MinLength/MaxLength). Nodes the segment passes over are
/// NOT considered occupied by this edge — per design, pass-through nodes stay free for other
/// sub-symbols to use independently; only actual geometric crossings are disallowed.
/// </summary>
public readonly struct GridEdge
{
    public GridPoint From { get; }
    public GridPoint To { get; }

    public GridEdge(GridPoint from, GridPoint to)
    {
        From = from;
        To = to;
    }

    /// <summary>
    /// Exact integer-geometry intersection test between two lattice segments. This is exact
    /// (no sampling/tolerance needed) because every endpoint of every edge is always an integer
    /// grid coordinate. Segments that only touch at a shared endpoint (chains connecting
    /// head-to-tail, or a closed loop's final edge back to its start) do NOT count as an
    /// intersection — only a genuine interior crossing, or an overlap extending beyond that
    /// single shared point, counts.
    /// </summary>
    public bool Intersects(GridEdge other)
    {
        GridPoint p1 = From, q1 = To, p2 = other.From, q2 = other.To;

        bool sharesEndpoint = p1 == p2 || p1 == q2 || q1 == p2 || q1 == q2;

        long o1 = Orientation(p1, q1, p2);
        long o2 = Orientation(p1, q1, q2);
        long o3 = Orientation(p2, q2, p1);
        long o4 = Orientation(p2, q2, q1);

        // General case: the two segments cross at a single point determined by opposite
        // orientations on both sides.
        if (o1 != o2 && o3 != o4)
        {
            // If that single crossing point IS a shared endpoint, it's a touch, not a cross.
            return !sharesEndpoint;
        }

        // Collinear special cases: true only if one segment's endpoint lies on the other.
        bool collinearOverlap =
            (o1 == 0 && OnSegment(p1, p2, q1)) ||
            (o2 == 0 && OnSegment(p1, q2, q1)) ||
            (o3 == 0 && OnSegment(p2, p1, q2)) ||
            (o4 == 0 && OnSegment(p2, q1, q2));

        if (!collinearOverlap) return false;
        if (!sharesEndpoint) return true; // genuine overlap with no shared endpoint to explain it

        // Collinear AND sharing an endpoint: only a real intersection if they overlap by more
        // than that single shared point (e.g. one edge doubling back over another), never for
        // two edges that simply continue in the same direction from a shared node.
        return CollinearOverlapLength(p1, q1, p2, q2) > 0;
    }

    /// <summary>Twice the signed area of triangle (a, b, c); its sign gives orientation and
    /// zero means collinear. Doubling avoids fractional results while keeping everything in
    /// exact integer arithmetic.</summary>
    private static long Orientation(GridPoint a, GridPoint b, GridPoint c)
    {
        long val = (long)(b.Y - a.Y) * (c.X - b.X) - (long)(b.X - a.X) * (c.Y - b.Y);
        return val == 0 ? 0 : (val > 0 ? 1 : -1);
    }

    /// <summary>Given a, b, c are already known to be collinear, checks whether b lies within
    /// the bounding box of segment a-c (equivalent to lying on it, for collinear points).</summary>
    private static bool OnSegment(GridPoint a, GridPoint b, GridPoint c) =>
        b.X <= Math.Max(a.X, c.X) && b.X >= Math.Min(a.X, c.X) &&
        b.Y <= Math.Max(a.Y, c.Y) && b.Y >= Math.Min(a.Y, c.Y);

    /// <summary>For two collinear segments, the length of their overlap projected onto
    /// whichever axis has the larger spread (avoids a degenerate zero-length projection on
    /// purely horizontal/vertical lines). Zero means they touch at a point at most.</summary>
    private static long CollinearOverlapLength(GridPoint p1, GridPoint q1, GridPoint p2, GridPoint q2)
    {
        bool useX = Math.Abs(q1.X - p1.X) >= Math.Abs(q1.Y - p1.Y);

        long a1 = useX ? p1.X : p1.Y, b1 = useX ? q1.X : q1.Y;
        long a2 = useX ? p2.X : p2.Y, b2 = useX ? q2.X : q2.Y;

        long min1 = Math.Min(a1, b1), max1 = Math.Max(a1, b1);
        long min2 = Math.Min(a2, b2), max2 = Math.Max(a2, b2);

        return Math.Max(0, Math.Min(max1, max2) - Math.Max(min1, min2));
    }

    public override string ToString() => $"{From} -> {To}";
}
